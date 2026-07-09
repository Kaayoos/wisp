using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Wisp.Models;
using Wisp.Plugins.Export;
using Wisp.Services;

namespace Wisp
{
    public partial class MainWindow : Window
    {
        // ================= VIDEO EXPORT AND TRIMMING =================

        private void UpdateTrimUI()
        {
            if (_activeClip == null || WaveformCanvas == null || LeftClamp == null || RightClamp == null || LeftDimOverlay == null || RightDimOverlay == null) return;

            double w = WaveformCanvas.ActualWidth;
            double h = WaveformCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Fixed grip width (matches the Thumb template). Using a constant instead of the clamp's ActualWidth
            // avoids a layout race that could place the right clamp at x≈w and shove it entirely past the clipped
            // canvas edge - the "right clamp sometimes doesn't appear" bug. Clamping Left into [0, w-cw] keeps both
            // grips fully on-canvas at the extremes, while the dim overlays still mark the exact trim boundary.
            const double cw = 16.0;
            double leftX = _trimStartFrac * w;
            double rightX = _trimEndFrac * w;
            double maxLeft = Math.Max(0, w - cw);

            LeftClamp.Height = h;
            Canvas.SetLeft(LeftClamp, Math.Clamp(leftX - cw / 2.0, 0, maxLeft));

            RightClamp.Height = h;
            Canvas.SetLeft(RightClamp, Math.Clamp(rightX - cw / 2.0, 0, maxLeft));

            // Left Dim Overlay
            LeftDimOverlay.Width = Math.Max(0, leftX);
            LeftDimOverlay.Height = h;
            Canvas.SetLeft(LeftDimOverlay, 0);

            // Right Dim Overlay
            RightDimOverlay.Width = Math.Max(0, w - rightX);
            RightDimOverlay.Height = h;
            Canvas.SetLeft(RightDimOverlay, rightX);

            UpdateTrimText();
        }

        // Trim clamps share the scrubber's anti-lag contract: pause playback for the drag, raise
        // _isUserSeeking so the A/V watchdog stands down, and THROTTLE the real seeks. Slamming
        // VideoPlayer.Position + mixer.Seek on every single mouse-move floods Media Foundation and
        // wedges the whole playback pipeline (frozen video, every later clip stuck until restart) -
        // and the watchdog then loops the audio over the frozen frame. This is the same fix the
        // waveform scrubber already had; the clamps just never got it.
        private void Clamp_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
        {
            _isUserSeeking = true;
            _lastScrubTicks = 0;
            if (_isPlaying)
            {
                VideoPlayer.Pause();
                if (_mixerActive) _audioMixer.Pause();
                _isPlaying = false;
                SetPlayPauseIcon(false);
            }
        }

        private void LeftClamp_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (_activeClip == null || WaveformCanvas == null) return;
            double w = WaveformCanvas.ActualWidth;
            if (w <= 0) return;

            double newX = _trimStartFrac * w + e.HorizontalChange;
            double maxX = _trimEndFrac * w - 15;           // keep at least 15px before the right clamp
            _trimStartFrac = Math.Clamp(newX, 0, maxX) / w;
            UpdateTrimUI();                                 // cheap visual update every move
            ThrottledTrimSeek(_trimStartFrac);              // the heavy seek is rate-limited
        }

        private void RightClamp_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (_activeClip == null || WaveformCanvas == null) return;
            double w = WaveformCanvas.ActualWidth;
            if (w <= 0) return;

            double newX = _trimEndFrac * w + e.HorizontalChange;
            double minX = _trimStartFrac * w + 15;          // keep at least 15px after the left clamp
            _trimEndFrac = Math.Clamp(newX, minX, w) / w;
            UpdateTrimUI();
            ThrottledTrimSeek(_trimEndFrac);
        }

        private void LeftClamp_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
            => EndClampDrag(_trimStartFrac);

        private void RightClamp_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
            => EndClampDrag(_trimEndFrac);

        // Rate-limited seek used while a clamp is being dragged (mirrors the scrubber's 80ms throttle).
        private void ThrottledTrimSeek(double frac)
        {
            long now = Environment.TickCount64;
            if (now - _lastScrubTicks < 80) return;
            _lastScrubTicks = now;
            SeekToTrimFrac(frac);
        }

        private void SeekToTrimFrac(double frac)
        {
            if (_activeClip == null) return;
            double duration = VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds : _activeClip.DurationSeconds;
            var pos = TimeSpan.FromSeconds(frac * duration);
            VideoPlayer.Position = pos;
            if (_mixerActive) _audioMixer.Seek(pos);
        }

        private void EndClampDrag(double frac)
        {
            SeekToTrimFrac(frac);   // final precise seek to the edge we set (the throttle may have skipped it)
            _isUserSeeking = false; // playback stays paused on the previewed boundary frame
            // The exportable region changed, so rebase the size slider to the trimmed clip's real size.
            if (_isExportSidebarOpen) UpdateExportSizeBounds();
        }

        private void UpdateTrimText()
        {
            if (_activeClip == null || TrimStartLabel == null || TrimEndLabel == null) return;
            
            double duration = VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds : _activeClip.DurationSeconds;
            double start = _trimStartFrac * duration;
            double end = _trimEndFrac * duration;
            
            TrimStartLabel.Text = $"{start:F1}s";
            TrimEndLabel.Text = $"{end:F1}s";
        }

        private void ExportBtn_Click(object sender, RoutedEventArgs e)
        {
            ToggleExportSidebar();
        }

        private void ToggleExportSidebar()
        {
            if (!_isExportSidebarOpen)
            {
                ExportSidebar.Visibility = Visibility.Visible;
                TrimCanvas.Visibility = Visibility.Visible;
                _isExportSidebarOpen = true;
                
                // Sliding open animation
                var slideAnim = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0,
                    To = 320,
                    Duration = TimeSpan.FromMilliseconds(300),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                ExportSidebar.BeginAnimation(FrameworkElement.WidthProperty, slideAnim);
                
                if (_isPlaying)
                {
                    VideoPlayer.Pause();
                    if (_mixerActive) _audioMixer.Pause();
                    _isPlaying = false;
                    SetPlayPauseIcon(false);
                }
                
                InitializeExportParams();
            }
            else
            {
                _isExportSidebarOpen = false;
                TrimCanvas.Visibility = Visibility.Collapsed;
                _verticalExport = false;
                if (CropOverlayHost != null) CropOverlayHost.Visibility = Visibility.Collapsed;

                // Sliding close animation
                var slideAnim = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 320,
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(250),
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
                };
                slideAnim.Completed += (s, e) => {
                    if (!_isExportSidebarOpen) ExportSidebar.Visibility = Visibility.Collapsed;
                };
                ExportSidebar.BeginAnimation(FrameworkElement.WidthProperty, slideAnim);
            }
        }

        private void InitializeExportParams()
        {
            if (_activeClip == null) return;
            
            string baseName = Path.GetFileNameWithoutExtension(_activeClip.Filename);
            ExportNameTextBox.Text = $"{baseName}_export";
            
            _trimStartFrac = 0.0;
            _trimEndFrac = 1.0;
            _vCropFracX = 0.5;
            _vCropFracY = 0.5;

            // "Apply audio mix" only means something when this clip has separate per-source tracks loaded
            // in the live mixer; legacy/imported clips have a single baked track, so there's nothing to remix.
            bool canMix = _mixerActive && _activeClip.HasSeparateAudio;
            ApplyAudioMixCheck.IsEnabled = canMix;
            ExportAudioSection.Opacity = canMix ? 1.0 : 0.5;
            ExportAudioHint.Text = canMix
                ? "Bake your mute & volume changes into the file."
                : "This clip has one combined track - nothing to remix.";

            // Default to landscape: paints the format chips, hides the crop chooser + custom inputs, and
            // refreshes the overlay-availability + format/metadata labels (overlays & resolution depend on
            // the format).
            SetExportFormat(false, false);

            // Size the slider against the trimmed region (full clip here, since trim just reset above).
            UpdateExportSizeBounds();
            UpdateTrimUI();
            // Re-run once layout settles, in case the waveform hadn't been measured yet on this first open -
            // otherwise both clamps would sit unpositioned until the next size change.
            Dispatcher.BeginInvoke(new Action(UpdateTrimUI), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ──────────────────── format selector (landscape / vertical 9:16 / custom crop) ────────────────────

        private void FormatLandscape_Click(object sender, MouseButtonEventArgs e) => SetExportFormat(false, false);
        private void FormatVertical_Click(object sender, MouseButtonEventArgs e) => SetExportFormat(true, false);
        private void FormatCustom_Click(object sender, MouseButtonEventArgs e) => SetExportFormat(true, true);

        // Switches the export between full-frame landscape, a 9:16 vertical crop, and a Custom W:H crop.
        // "crop" = a cropped aspect is active (vertical OR custom); "custom" picks the user-entered ratio
        // over the fixed 9:16. Paints the segmented chips, shows/hides the on-video crop chooser + the
        // custom-ratio inputs, and re-derives the dependent UI (overlays can't be burned into a crop in this
        // version; the format + resolution labels also change).
        private void SetExportFormat(bool crop, bool custom)
        {
            _verticalExport = crop;
            _customAspect = custom;
            _exportAspect = custom ? CurrentCustomAspect() : (9.0 / 16.0);

            bool land = !crop, vert = crop && !custom, cust = crop && custom;

            var accent = (Brush)FindResource("AccentBrush");
            var onText = new SolidColorBrush(Color.FromRgb(0x0A, 0x14, 0x14));   // dark ink on the accent chip
            var offText = new SolidColorBrush(Color.FromRgb(0x8A, 0x9A, 0x9B));  // muted on the transparent chip
            onText.Freeze(); offText.Freeze();

            if (FormatLandscapeChip != null) FormatLandscapeChip.Background = land ? accent : Brushes.Transparent;
            if (FormatVerticalChip != null) FormatVerticalChip.Background = vert ? accent : Brushes.Transparent;
            if (FormatCustomChip != null) FormatCustomChip.Background = cust ? accent : Brushes.Transparent;
            if (FormatLandscapeText != null) FormatLandscapeText.Foreground = land ? onText : offText;
            if (FormatVerticalText != null) FormatVerticalText.Foreground = vert ? onText : offText;
            if (FormatCustomText != null) FormatCustomText.Foreground = cust ? onText : offText;
            if (FormatLandscapeIcon != null) FormatLandscapeIcon.BorderBrush = land ? onText : offText;
            if (FormatVerticalIcon != null) FormatVerticalIcon.BorderBrush = vert ? onText : offText;
            if (FormatCustomIcon != null) FormatCustomIcon.BorderBrush = cust ? onText : offText;
            if (FormatHint != null) FormatHint.Visibility = crop ? Visibility.Visible : Visibility.Collapsed;
            if (CustomAspectPanel != null) CustomAspectPanel.Visibility = cust ? Visibility.Visible : Visibility.Collapsed;

            if (CropOverlayHost != null)
            {
                CropOverlayHost.Visibility = crop ? Visibility.Visible : Visibility.Collapsed;
                if (crop) LayoutCropOverlay();
            }

            UpdateExportFormatLabel();
            UpdateOverlayAvailability();
            UpdateExportMetadata();
        }

        // The custom crop's width/height from the two inputs, clamped to a sane range so an empty or
        // degenerate entry can never produce an invalid (or off-frame) crop. Falls back to 1:1.
        private double CurrentCustomAspect()
        {
            double w = ParsePositiveDouble(CustomAspectWBox?.Text, 1.0);
            double h = ParsePositiveDouble(CustomAspectHBox?.Text, 1.0);
            return Math.Clamp(w / h, 0.2, 5.0);
        }

        private static double ParsePositiveDouble(string? text, double fallback)
        {
            if (double.TryParse((text ?? "").Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0.0001)
                return v;
            return fallback;
        }

        // A tidy "W:H" label for the readout, using the typed values (e.g. "16:9"); falls back to "1:1".
        private string CustomAspectText()
        {
            string w = (CustomAspectWBox?.Text ?? "").Trim();
            string h = (CustomAspectHBox?.Text ?? "").Trim();
            return (string.IsNullOrEmpty(w) || string.IsNullOrEmpty(h)) ? "1:1" : $"{w}:{h}";
        }

        // Live-updates the crop as the user edits the ratio (only meaningful while Custom is active).
        private void CustomAspect_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_customAspect) return;
            _exportAspect = CurrentCustomAspect();
            LayoutCropOverlay();
            UpdateExportFormatLabel();
            UpdateExportMetadata();
        }

        // Quick-preset chip (Tag = "W:H"): fill the inputs, which re-drives the crop via TextChanged.
        private void AspectPreset_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
            var parts = tag.Split(':');
            if (parts.Length == 2 && CustomAspectWBox != null && CustomAspectHBox != null)
            {
                CustomAspectWBox.Text = parts[0].Trim();
                CustomAspectHBox.Text = parts[1].Trim();
            }
        }

        private void UpdateExportFormatLabel()
        {
            if (ExportFormatText == null) return;
            ExportFormatText.Text = !_verticalExport ? "MP4 (H.264 / AAC)"
                : _customAspect ? $"Custom {CustomAspectText()} (H.264 / AAC)"
                : "Vertical 9:16 (H.264 / AAC)";
        }

        // "Burn in overlays" is offered only when an enabled plugin actually contributes a layer for this clip
        // (e.g. the Camera Overlay plugin saved a webcam track) AND we're exporting landscape - overlay rects are
        // normalised to the full frame, so compositing them into a vertical crop isn't supported here. Otherwise
        // grey it out with a fitting hint.
        private void UpdateOverlayAvailability()
        {
            bool hasOverlays = _activeClip != null && _app.GetExportLayersForClip(_activeClip).Count > 0;
            bool allowed = hasOverlays && !_verticalExport;

            if (BurnOverlaysCheck != null)
            {
                BurnOverlaysCheck.IsEnabled = allowed;
                if (_verticalExport) BurnOverlaysCheck.IsChecked = false;
                else if (hasOverlays) BurnOverlaysCheck.IsChecked = true;
            }
            if (ExportOverlaySection != null) ExportOverlaySection.Opacity = allowed ? 1.0 : 0.5;
            if (ExportOverlayHint != null)
                ExportOverlayHint.Text = _verticalExport ? "Not available with a cropped aspect."
                    : hasOverlays ? "Bake plugin layers (e.g. webcam) into the video."
                    : "No plugin overlays for this clip.";
        }

        // ───────────────────────── 9:16 crop chooser (on-video preview) ─────────────────────────

        // Lays out the crop frame + dimmed scrim over the letterboxed video at the current crop position.
        // Cheap no-op unless the vertical format is active. Called when Vertical is selected and whenever the
        // displayed video rect changes (window/sidebar resize, media open).
        private void LayoutCropOverlay()
        {
            if (!_verticalExport || CropOverlayHost == null || CropFrame == null || CropScrim == null) return;

            var (vx, vy, vw, vh) = ComputeVideoRect();
            if (vw <= 1 || vh <= 1) return;

            var (cw, ch, cx, cy) = ComputeCropDisplayRect(vx, vy, vw, vh);

            CropFrame.Width = cw;
            CropFrame.Height = ch;
            Canvas.SetLeft(CropFrame, cx);
            Canvas.SetTop(CropFrame, cy);

            // Scrim = the video rect with the crop window punched out (EvenOdd fill), so everything that
            // WON'T be exported is dimmed.
            var grp = new GeometryGroup { FillRule = FillRule.EvenOdd };
            grp.Children.Add(new RectangleGeometry(new Rect(vx, vy, vw, vh)));
            grp.Children.Add(new RectangleGeometry(new Rect(cx, cy, cw, ch)));
            CropScrim.Data = grp;
        }

        // The crop window in display px for the current position + aspect, given the letterboxed video rect.
        private (double w, double h, double x, double y) ComputeCropDisplayRect(double vx, double vy, double vw, double vh)
        {
            double aspect = _exportAspect;
            double cw, ch;
            if (vw / vh > aspect) { ch = vh; cw = vh * aspect; }   // wide source → full height, narrow strip
            else { cw = vw; ch = vw / aspect; }                    // tall source → full width, short strip
            double slackX = vw - cw, slackY = vh - ch;
            return (cw, ch, vx + slackX * _vCropFracX, vy + slackY * _vCropFracY);
        }

        private void CropFrame_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _cropDragging = true;
            _cropDragStart = e.GetPosition(CropOverlayHost);
            _cropStartFracX = _vCropFracX;
            _cropStartFracY = _vCropFracY;
            CropFrame.CaptureMouse();
            e.Handled = true; // don't toggle play/pause on the video underneath
        }

        private void CropFrame_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_cropDragging) return;
            var (vx, vy, vw, vh) = ComputeVideoRect();
            if (vw <= 1 || vh <= 1) return;
            var (cw, ch, _, _) = ComputeCropDisplayRect(vx, vy, vw, vh);
            double slackX = vw - cw, slackY = vh - ch;
            var pos = e.GetPosition(CropOverlayHost);
            if (slackX > 0.5) _vCropFracX = Math.Clamp(_cropStartFracX + (pos.X - _cropDragStart.X) / slackX, 0.0, 1.0);
            if (slackY > 0.5) _vCropFracY = Math.Clamp(_cropStartFracY + (pos.Y - _cropDragStart.Y) / slackY, 0.0, 1.0);
            LayoutCropOverlay();
        }

        private void CropFrame_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_cropDragging) return;
            _cropDragging = false;
            CropFrame.ReleaseMouseCapture();
            e.Handled = true;
        }

        // The crop window in SOURCE pixels for the current position + aspect. All values are even (H.264
        // wants even dimensions/offsets) and clamped inside the frame.
        private (int w, int h, int x, int y) ComputeVerticalCrop(int srcW, int srcH)
        {
            double aspect = _exportAspect;
            int cropW, cropH;
            if ((double)srcW / srcH > aspect) { cropH = srcH; cropW = (int)Math.Round(srcH * aspect); }
            else { cropW = srcW; cropH = (int)Math.Round(srcW / aspect); }
            cropW = Math.Min(srcW, cropW & ~1);
            cropH = Math.Min(srcH, cropH & ~1);
            int slackX = srcW - cropW, slackY = srcH - cropH;
            int x = Math.Clamp((int)Math.Round(slackX * _vCropFracX) & ~1, 0, slackX);
            int y = Math.Clamp((int)Math.Round(slackY * _vCropFracY) & ~1, 0, slackY);
            return (cropW, cropH, x, y);
        }

        // Recompute the size slider against the CURRENTLY trimmed region. The exportable clip is only
        // [start,end], so its lossless size is the source bitrate x trimmed duration - NOT the whole
        // file. Without this, trimming a 4s slice of a 40s/30MB clip still offered a "10MB minimum",
        // so a 10MB target looked like compression even though an untouched lossless copy is only ~4MB.
        private void UpdateExportSizeBounds()
        {
            if (_activeClip == null || ExportTargetSlider == null) return;

            double fullSizeMB = _activeClip.FileSizeBytes / (1024.0 * 1024.0);
            double trimFrac = Math.Clamp(_trimEndFrac - _trimStartFrac, 0.0, 1.0);
            if (trimFrac <= 0) trimFrac = 1.0;
            _trimmedLosslessMB = fullSizeMB * trimFrac;     // ~size of a lossless -c copy of the trim

            const double floorMB = 2.0;                     // below this, re-encoding isn't worth offering
            ExportTargetSlider.ValueChanged -= ExportTargetSlider_ValueChanged;
            if (_trimmedLosslessMB <= floorMB + 1.0)
            {
                // Tiny clip - lossless only, no compression range.
                ExportTargetSlider.Minimum = _trimmedLosslessMB;
                ExportTargetSlider.Maximum = _trimmedLosslessMB;
                ExportTargetSlider.Value = _trimmedLosslessMB;
                ExportTargetSlider.IsEnabled = false;
            }
            else
            {
                ExportTargetSlider.Minimum = Math.Max(floorMB, _trimmedLosslessMB * 0.2);
                ExportTargetSlider.Maximum = _trimmedLosslessMB;   // can't be larger than a lossless copy
                ExportTargetSlider.Value = _trimmedLosslessMB;     // default sits at the top = lossless
                ExportTargetSlider.IsEnabled = true;
            }
            ExportTargetSlider.ValueChanged += ExportTargetSlider_ValueChanged;

            UpdateExportSizeLabel();
            UpdateExportMetadata();
        }

        // Lossless -c copy when the slider sits at the top of its range (the trim's natural size) or
        // compression is disabled for a tiny clip.
        private bool IsLosslessTarget()
            => !ExportTargetSlider.IsEnabled || (ExportTargetSlider.Maximum - ExportTargetSlider.Value) < 0.1;

        private void UpdateExportSizeLabel()
        {
            if (ExportTargetLabel == null) return;
            ExportTargetLabel.Text = IsLosslessTarget()
                ? $"Original (Source Quality - {_trimmedLosslessMB:F1} MB)"
                : $"{ExportTargetSlider.Value:F1} MB";
        }

        private void UpdateExportMetadata()
        {
            if (_activeClip == null) return;

            int nvw = VideoPlayer.NaturalVideoWidth, nvh = VideoPlayer.NaturalVideoHeight;
            if (_verticalExport && nvw > 0 && nvh > 0)
            {
                var (cw, ch, _, _) = ComputeVerticalCrop(nvw, nvh);
                ExportResText.Text = $"{cw}x{ch}";
            }
            else if (nvw > 0 && nvh > 0)
                ExportResText.Text = $"{nvw}x{nvh}";
            else
                ExportResText.Text = "Unknown";

            // A vertical crop is a filtered stream, so it's always a re-encode - never the instant copy path.
            if (IsLosslessTarget() && !_verticalExport)
            {
                ExportTimeText.Text = "Instant (Lossless Copy)";
            }
            else
            {
                double duration = _activeClip.DurationSeconds * (_trimEndFrac - _trimStartFrac);
                double est = Math.Max(2.0, duration * 0.2);
                ExportTimeText.Text = $"~{est:F0} Seconds (Re-encoding)";
            }
        }

        private void ExportTargetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_activeClip == null || ExportTargetLabel == null) return;
            UpdateExportSizeLabel();
            UpdateExportMetadata();
        }

        private async void ExportClipBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_activeClip == null) return;

            string proposedName = ExportNameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(proposedName))
            {
                CustomMessageBox.Show("Please enter a valid clip name.", "Export Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Clean up proposed name for files
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                proposedName = proposedName.Replace(c, '_');
            }

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                FileName = !_verticalExport ? proposedName
                         : _customAspect ? proposedName + "_custom"
                         : proposedName + "_vertical",
                DefaultExt = ".mp4",
                Filter = "MP4 Video (*.mp4)|*.mp4"
            };

            if (sfd.ShowDialog() != true) return;
            string destPath = sfd.FileName;

            double duration = VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds : _activeClip.DurationSeconds;
            double start = _trimStartFrac * duration;
            double end = _trimEndFrac * duration;
            double trimDuration = end - start;

            if (trimDuration <= 0.1)
            {
                CustomMessageBox.Show("Trim range is too small (must be at least 0.1s).", "Export Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            double targetSizeMB = ExportTargetSlider.Value;

            // Lossless when the slider is at the top of its (trim-relative) range; otherwise re-encode.
            bool isLossless = IsLosslessTarget();

            string ffmpegPath = _app.RecorderService.FFmpegExePath;
            if (!File.Exists(ffmpegPath))
            {
                CustomMessageBox.Show("FFmpeg binary not found. Try restarting Wisp.", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Plugin export layers to burn in (e.g. a webcam PiP), plus the output resolution they position
            // against - captured NOW, because releasing the MediaElement below zeroes NaturalVideoWidth/Height.
            int overlayOutW = VideoPlayer.NaturalVideoWidth;
            int overlayOutH = VideoPlayer.NaturalVideoHeight;
            var exportLayers = (BurnOverlaysCheck?.IsChecked == true)
                ? _app.GetExportLayersForClip(_activeClip).Where(l => l != null && File.Exists(l.SourcePath)).ToList()
                : new List<ExportLayer>();

            // CRITICAL: Release the file handle held by MediaElement so FFmpeg can read it.
            // Store the playback position so we can restore after export.
            var savedPosition = VideoPlayer.Position;
            bool wasPlaying = _isPlaying;
            _playerTimer?.Stop();
            if (_mixerActive) _audioMixer.Pause();
            VideoPlayer.Stop();
            VideoPlayer.Source = null;

            // Apply the player's per-track mute/volume mix into the export when the user asked for it and
            // this clip actually has separate tracks to remix.
            bool applyMix = ApplyAudioMixCheck.IsChecked == true && _mixerActive && _activeClip.HasSeparateAudio;

            // Show Progress overlay
            ExportProgressOverlay.Visibility = Visibility.Visible;
            ExportProgressStatus.Text = _verticalExport ? "Rendering cropped clip (re-encoding)..."
                                      : exportLayers.Count > 0 ? "Compositing overlays (re-encoding)..."
                                      : !isLossless ? "Compressing clip (re-encoding)..."
                                      : applyMix ? "Applying audio mix..."
                                      : "Extracting clip (lossless)...";

            string sourcePath = _activeClip.FilePath;
            // Vertical 9:16 takes its own crop+encode path (overlays are forced off for it above); everything
            // else goes through the battle-tested copy / remix / overlay builder.
            string args = (_verticalExport && overlayOutW > 0 && overlayOutH > 0)
                ? BuildVerticalExportArgs(sourcePath, destPath, start, trimDuration, isLossless, targetSizeMB, applyMix, overlayOutW, overlayOutH)
                : BuildExportArgs(sourcePath, destPath, start, trimDuration, isLossless, targetSizeMB, applyMix,
                                  exportLayers, overlayOutW, overlayOutH);

            bool success;
            string errorOutput;
            try
            {
                (success, errorOutput) = await RunFFmpegExportAsync(ffmpegPath, args);
            }
            finally
            {
                // ALWAYS restore the player and hide the overlay - even on failure/timeout/exception -
                // so the player can never be left stuck with a null source (the old "no other video can
                // be played until I restart the app" bug).
                if (_activeClip != null && File.Exists(_activeClip.FilePath))
                {
                    VideoPlayer.Source = new Uri(_activeClip.FilePath);
                    VideoPlayer.Play();
                    VideoPlayer.Position = savedPosition;
                    VideoPlayer.Pause();
                    _isPlaying = false;
                    SetPlayPauseIcon(false);
                    _playerTimer?.Start();
                }
                ExportProgressOverlay.Visibility = Visibility.Collapsed;
            }

            if (success && File.Exists(destPath) && new FileInfo(destPath).Length > 0)
            {
                _exportedClipPath = destPath;
                ExportedFileNameLabel.Text = Path.GetFileName(destPath);
                ExportSuccessPopup.Visibility = Visibility.Visible;
            }
            else
            {
                string detail = string.IsNullOrWhiteSpace(errorOutput) ? "Check if the source file is accessible." : errorOutput;
                if (detail.Length > 300) detail = detail.Substring(detail.Length - 300);
                CustomMessageBox.Show($"Export failed.\n\n{detail}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Builds the FFmpeg argument string for an export. When applyMix is set (the clip has separate
        // per-source tracks and the user kept "Apply audio mix" on), the .m4a sidecars are re-mixed using
        // the live mixer's current per-track mute/volume and muxed over the video; otherwise the clip's own
        // baked stereo audio is used - the original behavior, byte-for-byte.
        //
        // The video stays a stream copy in lossless mode (only the audio is rebuilt), so a from-the-start
        // export is still effectively instant. As with the existing lossless trim, a trimmed (start > 0)
        // copy seeks to the nearest keyframe, so a remixed trim carries that same small boundary imprecision.
        private string BuildExportArgs(string sourcePath, string destPath, double start, double dur,
                                       bool isLossless, double targetSizeMB, bool applyMix,
                                       IReadOnlyList<ExportLayer> layers, int outW, int outH)
        {
            // Overlay burn-in: when a plugin contributes export layers (e.g. a webcam PiP) and we know the
            // output size, composite them with ffmpeg (always a re-encode - a filtered stream can't be a copy).
            // No layers → fall through to the original, untouched copy/remix paths below.
            if (layers != null && layers.Count > 0 && outW > 0 && outH > 0)
                return BuildExportArgsWithOverlays(sourcePath, destPath, start, dur, isLossless, targetSizeMB, applyMix, layers, outW, outH);

            // Audio bitrate for every re-encoded path here follows the user's Audio Quality setting, so an
            // exported clip sounds like the recorded one. The size-target math subtracts it from the video
            // budget so a chosen file size is still honoured.
            int aKbps = _app.Settings.AudioBitrateKbps;

            // Collect the present per-source tracks at the volume the user currently has them (slider %,
            // forced to 0 when muted). Input 0 is always the source video.
            var audioInputs = new List<string>();
            var tracks = new List<(int idx, double vol)>();
            if (applyMix)
            {
                int idx = 1;
                void AddTrack(string? path, bool has, double sliderPercent, bool muted)
                {
                    if (!has || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                    audioInputs.Add(System.FormattableString.Invariant($"-ss {start:F3} -i \"{path}\""));
                    tracks.Add((idx, muted ? 0.0 : sliderPercent / 100.0));
                    idx++;
                }
                AddTrack(_activeClip!.SystemTrackPath, _audioMixer.HasSystem, SystemVolSlider.Value, SystemMuteToggle.IsChecked == true);
                AddTrack(_activeClip!.MicTrackPath, _audioMixer.HasMic, MicVolSlider.Value, MicMuteToggle.IsChecked == true);
                AddTrack(_activeClip!.SocialTrackPath, _audioMixer.HasSocial, SocialVolSlider.Value, SocialMuteToggle.IsChecked == true);
            }

            // No remix to do (option off, or no usable sidecars): the original single-input behavior.
            if (tracks.Count == 0)
            {
                if (isLossless)
                    return System.FormattableString.Invariant($"-ss {start:F3} -i \"{sourcePath}\" -t {dur:F3} -c copy -avoid_negative_ts make_zero -y \"{destPath}\"");

                double vbr0 = ((targetSizeMB * 8192.0) / dur) - aKbps;
                if (vbr0 < 500) vbr0 = 500;
                return System.FormattableString.Invariant($"-ss {start:F3} -i \"{sourcePath}\" -t {dur:F3} -c:v libx264 -b:v {vbr0:F0}k -preset superfast -pix_fmt yuv420p -c:a aac -b:a {aKbps}k -y \"{destPath}\"");
            }

            // Remix path: volume-scale each track, then amix them. normalize=0 keeps absolute levels - the
            // default divides every input by the track count, which would quietly drop the whole mix.
            string filter;
            if (tracks.Count == 1)
            {
                filter = System.FormattableString.Invariant($"[{tracks[0].idx}:a]volume={tracks[0].vol:F3}[aout]");
            }
            else
            {
                var parts = tracks.Select(t => System.FormattableString.Invariant($"[{t.idx}:a]volume={t.vol:F3}[a{t.idx}]"));
                string labels = string.Concat(tracks.Select(t => $"[a{t.idx}]"));
                filter = string.Join(";", parts) + ";" + labels +
                         System.FormattableString.Invariant($"amix=inputs={tracks.Count}:normalize=0[aout]");
            }

            // A copied video stream can only start on a keyframe. The freshly-mixed audio starts exactly
            // at the trim point, so for a trimmed clip (start > 0) a stream copy would drift out of sync by
            // up to the GOP length (the recorder uses a 1s keyframe interval). From the very start the
            // keyframe IS at 0, so a copy stays perfectly aligned. Hence: copy only an untrimmed lossless
            // export; re-encode (frame-accurate, so A/V stay locked) whenever we trim or compress.
            bool trimmedStart = start > 0.05;
            string videoCodec, audioCodec, tail = "";
            if (isLossless && !trimmedStart)
            {
                videoCodec = "-c:v copy";
                audioCodec = System.FormattableString.Invariant($"-c:a aac -b:a {aKbps}k");   // video is untouched, so spend bits on clean audio
                tail = "-avoid_negative_ts make_zero";
            }
            else if (isLossless)
            {
                // Trimmed but "original quality": re-encode near-losslessly to keep the picture locked to
                // the remixed audio.
                videoCodec = "-c:v libx264 -preset superfast -crf 18 -pix_fmt yuv420p";
                audioCodec = System.FormattableString.Invariant($"-c:a aac -b:a {aKbps}k");
            }
            else
            {
                double vbr = ((targetSizeMB * 8192.0) / dur) - aKbps;
                if (vbr < 500) vbr = 500;
                videoCodec = System.FormattableString.Invariant($"-c:v libx264 -b:v {vbr:F0}k -preset superfast -pix_fmt yuv420p");
                audioCodec = System.FormattableString.Invariant($"-c:a aac -b:a {aKbps}k");
            }

            string inputs = System.FormattableString.Invariant($"-ss {start:F3} -i \"{sourcePath}\" ") + string.Join(" ", audioInputs);
            return System.FormattableString.Invariant(
                $"{inputs} -t {dur:F3} -filter_complex \"{filter}\" -map 0:v -map \"[aout]\" {videoCodec} {audioCodec} {tail} -y \"{destPath}\"");
        }

        // Builds the FFmpeg args for an export that burns plugin overlay layers into the video. Input 0 is the
        // source (seeked to the trim start); optional audio sidecars follow (when remixing); then one input per
        // layer. A single -filter_complex scales/positions each layer with overlay= and, when remixing, mixes
        // the audio sidecars - producing [vout] (+ [aout]).
        private string BuildExportArgsWithOverlays(string sourcePath, string destPath, double start, double dur,
                                                   bool isLossless, double targetSizeMB, bool applyMix,
                                                   IReadOnlyList<ExportLayer> layers, int outW, int outH)
        {
            var inputs = new List<string> { System.FormattableString.Invariant($"-ss {start:F3} -i \"{sourcePath}\"") };
            int idx = 1;

            // Optional audio remix sidecars (same selection/levels as the non-overlay remix path).
            var tracks = new List<(int idx, double vol)>();
            if (applyMix)
            {
                void AddTrack(string? path, bool has, double sliderPercent, bool muted)
                {
                    if (!has || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                    inputs.Add(System.FormattableString.Invariant($"-ss {start:F3} -i \"{path}\""));
                    tracks.Add((idx, muted ? 0.0 : sliderPercent / 100.0));
                    idx++;
                }
                AddTrack(_activeClip!.SystemTrackPath, _audioMixer.HasSystem, SystemVolSlider.Value, SystemMuteToggle.IsChecked == true);
                AddTrack(_activeClip!.MicTrackPath, _audioMixer.HasMic, MicVolSlider.Value, MicMuteToggle.IsChecked == true);
                AddTrack(_activeClip!.SocialTrackPath, _audioMixer.HasSocial, SocialVolSlider.Value, SocialMuteToggle.IsChecked == true);
            }

            // One input per usable layer. Video layers are seeked to the trim start so they stay synced with
            // the trimmed clip; image layers are looped to span it.
            var layerInputs = new List<(int idx, ExportLayer layer)>();
            foreach (var layer in layers)
            {
                if (layer == null || string.IsNullOrEmpty(layer.SourcePath) || !File.Exists(layer.SourcePath)) continue;
                inputs.Add(IsImageLayer(layer.SourcePath)
                    ? System.FormattableString.Invariant($"-loop 1 -i \"{layer.SourcePath}\"")
                    : System.FormattableString.Invariant($"-ss {start:F3} -i \"{layer.SourcePath}\""));
                layerInputs.Add((idx, layer));
                idx++;
            }

            // No usable layer survived the existence checks → just re-encode without an overlay.
            if (layerInputs.Count == 0)
                return BuildExportArgs(sourcePath, destPath, start, dur, isLossless, targetSizeMB, applyMix, System.Array.Empty<ExportLayer>(), 0, 0);

            // Video filtergraph: scale → (mirror) → (opacity) each layer, then chain overlay= onto the base.
            var parts = new List<string>();
            string baseLabel = "0:v";
            for (int n = 0; n < layerInputs.Count; n++)
            {
                var (li, layer) = layerInputs[n];
                int w = Math.Max(2, (int)Math.Round(layer.Width * outW));
                int h = layer.Height > 0 ? Math.Max(2, (int)Math.Round(layer.Height * outH)) : -2; // -2 keeps aspect (even)
                int x = (int)Math.Round(layer.X * outW);
                int y = (int)Math.Round(layer.Y * outH);

                string scaled = $"ov{n}";
                string chain = System.FormattableString.Invariant($"[{li}:v]scale={w}:{h}");
                if (layer.Mirror) chain += ",hflip";
                if (layer.Opacity < 0.999)
                    chain += System.FormattableString.Invariant($",format=rgba,colorchannelmixer=aa={Math.Clamp(layer.Opacity, 0, 1):F3}");
                chain += $"[{scaled}]";
                parts.Add(chain);

                string outLabel = (n == layerInputs.Count - 1) ? "vout" : $"vb{n}";
                parts.Add(System.FormattableString.Invariant($"[{baseLabel}][{scaled}]overlay={x}:{y}[{outLabel}]"));
                baseLabel = outLabel;
            }
            string videoFilter = string.Join(";", parts);

            // Audio: remix sidecars to [aout], or pass the clip's own track through (re-encoded to AAC).
            string audioFilter = "";
            string audioMap;
            if (tracks.Count == 1)
            {
                audioFilter = System.FormattableString.Invariant($"[{tracks[0].idx}:a]volume={tracks[0].vol:F3}[aout]");
                audioMap = "-map \"[aout]\"";
            }
            else if (tracks.Count > 1)
            {
                var vol = tracks.Select(t => System.FormattableString.Invariant($"[{t.idx}:a]volume={t.vol:F3}[a{t.idx}]"));
                string labels = string.Concat(tracks.Select(t => $"[a{t.idx}]"));
                audioFilter = string.Join(";", vol) + ";" + labels +
                              System.FormattableString.Invariant($"amix=inputs={tracks.Count}:normalize=0[aout]");
                audioMap = "-map \"[aout]\"";
            }
            else
            {
                audioMap = "-map 0:a?"; // no remix → keep the clip's baked stereo track (optional)
            }

            string filter = videoFilter + (audioFilter.Length > 0 ? ";" + audioFilter : "");

            // The video is filtered, so always re-encode it. "Original quality" → near-lossless CRF; otherwise
            // hit the size target. Audio is AAC at the user's Audio Quality bitrate (source track or remix).
            int aKbps = _app.Settings.AudioBitrateKbps;
            double vbr = Math.Max(500, (targetSizeMB * 8192.0 / dur) - aKbps);
            string videoCodec = isLossless
                ? "-c:v libx264 -preset superfast -crf 18 -pix_fmt yuv420p"
                : System.FormattableString.Invariant($"-c:v libx264 -b:v {vbr:F0}k -preset superfast -pix_fmt yuv420p");
            string audioCodec = System.FormattableString.Invariant($"-c:a aac -b:a {aKbps}k");

            return System.FormattableString.Invariant(
                $"{string.Join(" ", inputs)} -t {dur:F3} -filter_complex \"{filter}\" -map \"[vout]\" {audioMap} {videoCodec} {audioCodec} -y \"{destPath}\"");
        }

        private static readonly string[] LayerImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };
        private static bool IsImageLayer(string path)
        {
            string ext = Path.GetExtension(path);
            foreach (var e in LayerImageExtensions)
                if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Builds the FFmpeg args for a 9:16 vertical export: crop the chosen window out of the source and
        // re-encode (a crop is a filtered stream, so it can never be a stream copy). Optional audio remix uses
        // the same per-track logic as the landscape path. Output keeps the crop's native (even) resolution, so
        // there's no upscale; the size slider still drives the bitrate when the user wants a smaller file.
        private string BuildVerticalExportArgs(string sourcePath, string destPath, double start, double dur,
                                               bool isLossless, double targetSizeMB, bool applyMix, int srcW, int srcH)
        {
            var (cropW, cropH, cropX, cropY) = ComputeVerticalCrop(srcW, srcH);
            string videoFilter = System.FormattableString.Invariant($"[0:v]crop={cropW}:{cropH}:{cropX}:{cropY},setsar=1[vout]");

            var inputs = new List<string> { System.FormattableString.Invariant($"-ss {start:F3} -i \"{sourcePath}\"") };
            var (audioInputs, audioFilter, audioMap) = BuildAudioRemix(applyMix, start, 1);
            inputs.AddRange(audioInputs);

            string filter = videoFilter + (audioFilter.Length > 0 ? ";" + audioFilter : "");

            int aKbps = _app.Settings.AudioBitrateKbps;
            double vbr = Math.Max(500, (targetSizeMB * 8192.0 / dur) - aKbps);
            string videoCodec = isLossless
                ? "-c:v libx264 -preset superfast -crf 18 -pix_fmt yuv420p"
                : System.FormattableString.Invariant($"-c:v libx264 -b:v {vbr:F0}k -preset superfast -pix_fmt yuv420p");
            string audioCodec = System.FormattableString.Invariant($"-c:a aac -b:a {aKbps}k");

            return System.FormattableString.Invariant(
                $"{string.Join(" ", inputs)} -t {dur:F3} -filter_complex \"{filter}\" -map \"[vout]\" {audioMap} {videoCodec} {audioCodec} -y \"{destPath}\"");
        }

        // Shared audio side of the re-encode export paths. Returns the extra "-ss .. -i sidecar" inputs (added
        // after the video input, numbered from firstSidecarIdx), the filter that produces [aout] (empty when not
        // remixing), and the -map for audio: "[aout]" when remixing, else the source's own baked track. Mirrors
        // the volume/mute selection used by the landscape remix paths so every export sounds identical.
        private (List<string> inputs, string filter, string map) BuildAudioRemix(bool applyMix, double start, int firstSidecarIdx)
        {
            var inputs = new List<string>();
            var tracks = new List<(int idx, double vol)>();
            if (applyMix)
            {
                int idx = firstSidecarIdx;
                void AddTrack(string? path, bool has, double sliderPercent, bool muted)
                {
                    if (!has || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                    inputs.Add(System.FormattableString.Invariant($"-ss {start:F3} -i \"{path}\""));
                    tracks.Add((idx, muted ? 0.0 : sliderPercent / 100.0));
                    idx++;
                }
                AddTrack(_activeClip!.SystemTrackPath, _audioMixer.HasSystem, SystemVolSlider.Value, SystemMuteToggle.IsChecked == true);
                AddTrack(_activeClip!.MicTrackPath, _audioMixer.HasMic, MicVolSlider.Value, MicMuteToggle.IsChecked == true);
                AddTrack(_activeClip!.SocialTrackPath, _audioMixer.HasSocial, SocialVolSlider.Value, SocialMuteToggle.IsChecked == true);
            }

            if (tracks.Count == 0) return (inputs, "", "-map 0:a?");
            if (tracks.Count == 1)
                return (inputs, System.FormattableString.Invariant($"[{tracks[0].idx}:a]volume={tracks[0].vol:F3}[aout]"), "-map \"[aout]\"");

            var vol = tracks.Select(t => System.FormattableString.Invariant($"[{t.idx}:a]volume={t.vol:F3}[a{t.idx}]"));
            string labels = string.Concat(tracks.Select(t => $"[a{t.idx}]"));
            string f = string.Join(";", vol) + ";" + labels +
                       System.FormattableString.Invariant($"amix=inputs={tracks.Count}:normalize=0[aout]");
            return (inputs, f, "-map \"[aout]\"");
        }

        private async Task<(bool success, string errorOutput)> RunFFmpegExportAsync(string ffmpegPath, string args)
        {
            return await System.Threading.Tasks.Task.Run(() =>
            {
                Process? process = null;
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpegPath.Replace('\\', '/'),
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };

                    process = Process.Start(psi);
                    if (process == null) return (false, "Failed to start FFmpeg process.");

                    ChildProcessTracker.AddProcess(process);

                    // FFmpeg writes ALL of its progress/logging to stderr. We must drain stdout AND
                    // stderr CONCURRENTLY: reading one stream to completion before the other lets the
                    // unread pipe buffer (~4 KB) fill during a re-encode, at which point FFmpeg blocks
                    // on write and we deadlock forever. Async event-based reads pump both at once.
                    var stderrBuffer = new System.Text.StringBuilder();
                    process.OutputDataReceived += (s, e) => { /* drain stdout so its buffer never fills */ };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null) stderrBuffer.AppendLine(e.Data);
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    // Hard safety net: a wedged encode can never hang the app again.
                    const int timeoutMs = 10 * 60 * 1000; // 10 minutes
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(true); } catch { /* best effort */ }
                        return (false, "Export timed out and was cancelled.");
                    }
                    // Flush any outstanding async output callbacks before reading the buffer.
                    process.WaitForExit();

                    string stderr = stderrBuffer.ToString();
                    Debug.WriteLine($"FFmpeg exit code: {process.ExitCode}");
                    if (!string.IsNullOrWhiteSpace(stderr))
                        Debug.WriteLine($"FFmpeg stderr: {stderr}");

                    return (process.ExitCode == 0, stderr);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FFmpeg export exception: {ex.Message}");
                    try { if (process != null && !process.HasExited) process.Kill(true); } catch { }
                    return (false, ex.Message);
                }
                finally
                {
                    process?.Dispose();
                }
            });
        }

        private void CloseExportPopupBtn_Click(object sender, RoutedEventArgs e)
        {
            ExportSuccessPopup.Visibility = Visibility.Collapsed;
            _exportedClipPath = "";
        }

        private void ExportDragCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(ExportDragCard);
            _dragOccurred = false;
        }

        private void ExportDragCard_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && !string.IsNullOrEmpty(_exportedClipPath) && File.Exists(_exportedClipPath))
            {
                Point currentPoint = e.GetPosition(ExportDragCard);
                Vector diff = _dragStartPoint - currentPoint;
                
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _dragOccurred = true;
                    
                    var dataObject = new DataObject();
                    var filePaths = new System.Collections.Specialized.StringCollection { _exportedClipPath };
                    dataObject.SetFileDropList(filePaths);
                    
                    DragDrop.DoDragDrop(ExportDragCard, dataObject, DragDropEffects.Copy);
                }
            }
        }

        private void ExportDragCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragOccurred && !string.IsNullOrEmpty(_exportedClipPath) && File.Exists(_exportedClipPath))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _exportedClipPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Show($"Failed to open clip: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            _dragOccurred = false;
        }
    }
}
