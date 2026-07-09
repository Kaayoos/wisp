using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Wisp.Models;
using Wisp.Services;

namespace Wisp
{
    public partial class MainWindow : Window
    {
        private void PlayClip(Clip clip)
        {
            if (File.Exists(clip.FilePath))
            {
                try
                {
                    _activeClip = clip;
                    PlayerOverlay.DataContext = clip;
                    
                    // Fade in PlayerOverlay
                    PlayerOverlay.Opacity = 0;
                    PlayerOverlay.Visibility = Visibility.Visible;
                    var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 0.0,
                        To = 1.0,
                        Duration = new Duration(TimeSpan.FromSeconds(0.25))
                    };
                    PlayerOverlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                    
                    // Set up the live audio mixer (separate system/mic tracks) before starting playback.
                    // The mixer is actually started in VideoPlayer_MediaOpened so it begins with the video.
                    SetupMixerForClip(clip);

                    VideoPlayer.Source = new Uri(clip.FilePath);
                    VideoPlayer.Play();
                    _isPlaying = true;
                    SetPlayPauseIcon(true);

                    // Draw the per-source waveforms under the video (peaks computed off the UI thread).
                    LoadWaveform(clip);

                    if (_playerTimer == null)
                    {
                        _playerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                        _playerTimer.Tick += PlayerTimer_Tick;
                    }
                    _playerTimer.Start();

                    // Plugin player surface: start each clip with a clean timeline, show plugin buttons, then
                    // notify plugins (their ClipOpened handler paints this clip's markers - see IWispPlayer).
                    _app.ClearAllPlayerMarkers();
                    RefreshPlayerPluginButtons();
                    _app.RaisePlayerOpened(clip);
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Show($"Failed to play video: {ex.Message}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                CustomMessageBox.Show("Clip file not found on disk. It may have been moved or deleted externally.", "File Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                LoadClips(); // Refresh database status
            }
        }

        private void ClosePlayer()
        {
            // Let plugins react while the clip is still active, then drop their markers for the next clip.
            if (_activeClip != null)
            {
                _app.RaisePlayerClosed();
                _app.ClearAllPlayerMarkers();
            }

            _activeClip = null;
            PlayerOverlay.DataContext = null;
            _isExportSidebarOpen = false;
            _verticalExport = false;
            if (ExportSidebar != null) ExportSidebar.Visibility = Visibility.Collapsed;
            if (TrimCanvas != null) TrimCanvas.Visibility = Visibility.Collapsed;
            if (CropOverlayHost != null) CropOverlayHost.Visibility = Visibility.Collapsed;
            if (ExportProgressOverlay != null) ExportProgressOverlay.Visibility = Visibility.Collapsed;
            if (ExportSuccessPopup != null) ExportSuccessPopup.Visibility = Visibility.Collapsed;

            _playerTimer?.Stop();
            VideoPlayer.Stop();
            VideoPlayer.Source = null; // Free file handle!

            // Tear down the live mixer and waveform.
            _audioMixer.Dispose();
            _mixerActive = false;
            MixerPopup.IsOpen = false;
            _waveformToken++; // cancel any in-flight peak computation
            _laneData.Clear();
            WaveformCanvas.Children.Clear();
            _chainMarkerVisuals.Clear();
            _killMarkerVisuals.Clear();
            _pluginMarkerVisuals.Clear();
            ClearPlayerOverlayVisuals();
            _playhead = null;

            // Fade out PlayerOverlay
            var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 1.0,
                To = 0.0,
                Duration = new Duration(TimeSpan.FromSeconds(0.2))
            };
            fadeOut.Completed += (s, e) =>
            {
                PlayerOverlay.Visibility = Visibility.Collapsed;
            };
            PlayerOverlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            
            _isPlaying = false;
        }

        private void ClosePlayerBtn_Click(object sender, RoutedEventArgs e)
        {
            ClosePlayer();
        }

        private void VideoCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            TogglePlayPause();
        }

        private void TogglePlayPause()
        {
            if (_isPlaying)
            {
                VideoPlayer.Pause();
                if (_mixerActive) _audioMixer.Pause();
                _isPlaying = false;
                SetPlayPauseIcon(false);
            }
            else
            {
                VideoPlayer.Play();
                if (_mixerActive)
                {
                    _audioMixer.Seek(VideoPlayer.Position); // resync on resume
                    _audioMixer.Play();
                }
                _isPlaying = true;
                SetPlayPauseIcon(true);
            }
        }

        private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
        }

        private void FrameBackBtn_Click(object sender, RoutedEventArgs e)
        {
            StepFrame(-1);
        }

        private void FrameForwardBtn_Click(object sender, RoutedEventArgs e)
        {
            StepFrame(1);
        }

        private void StepFrame(int direction)
        {
            if (VideoPlayer == null) return;
            
            // Pause the video first so the frame skip is visible
            if (_isPlaying)
            {
                TogglePlayPause();
            }

            double frameTimeSeconds = 0.033; // ~30 fps frame duration
            var newPosition = VideoPlayer.Position + TimeSpan.FromSeconds(direction * frameTimeSeconds);
            
            if (newPosition < TimeSpan.Zero)
                newPosition = TimeSpan.Zero;
            else if (VideoPlayer.NaturalDuration.HasTimeSpan && newPosition > VideoPlayer.NaturalDuration.TimeSpan)
                newPosition = VideoPlayer.NaturalDuration.TimeSpan;

            VideoPlayer.Position = newPosition;
            if (_mixerActive) _audioMixer.Seek(newPosition);
            UpdateTimeText(newPosition, VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan : TimeSpan.Zero);
            UpdatePlayhead(VideoPlayer.NaturalDuration.HasTimeSpan && VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds > 0
                ? newPosition.TotalSeconds / VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds : 0);
        }

        private void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Stop();
            if (_mixerActive)
            {
                _audioMixer.Stop();
                _audioMixer.Seek(TimeSpan.Zero);
            }
            _isPlaying = false;
            SetPlayPauseIcon(false);
            UpdatePlayhead(0);
            UpdateTimeText(TimeSpan.Zero, VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan : TimeSpan.Zero);
        }

        private void MuteBtn_Click(object sender, RoutedEventArgs e)
        {
            // Toggling the slider value drives VolumeSlider_ValueChanged, which applies everything.
            if (_isMuted)
            {
                VolumeSlider.Value = _lastVolume > 0 ? _lastVolume : 0.8;
            }
            else
            {
                _lastVolume = VolumeSlider.Value > 0 ? VolumeSlider.Value : 0.8;
                VolumeSlider.Value = 0;
            }
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ApplyMasterVolume();
            if (VolumeSlider.Value > 0)
            {
                _isMuted = false;
                _lastVolume = VolumeSlider.Value;
                SetVolumeIcon(false);
            }
            else
            {
                _isMuted = true;
                SetVolumeIcon(true);
            }
        }

        // Master volume routes to the mixer when a clip has separate tracks (the video stays muted),
        // otherwise it controls the video element's own audio.
        private void ApplyMasterVolume()
        {
            double v = VolumeSlider?.Value ?? 0.8;
            if (_mixerActive)
            {
                VideoPlayer.Volume = 0.0;
                _audioMixer.SetMasterVolume((float)v);
                _audioMixer.SetMasterMuted(false);
            }
            else if (VideoPlayer != null)
            {
                VideoPlayer.Volume = v;
            }
        }

        private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                UpdateTimeText(TimeSpan.Zero, VideoPlayer.NaturalDuration.TimeSpan);
                // Duration is only known now, so (re)draw chain + kill + plugin markers with correct positions.
                DrawChainMarkers();
                DrawKillMarkers();
                DrawPluginMarkers();
            }
            // The natural video size is known now, so overlays can sit on the real (letterboxed) video rect.
            RepositionPlayerOverlays();
            LayoutCropOverlay(); // and the 9:16 crop chooser, if vertical export is active
            ShowVideoStats();    // populate the stats strip above the picture
            _isPlaying = true;
            SetPlayPauseIcon(true);

            // Start the live mixer together with the video (it was loaded in SetupMixerForClip).
            if (_mixerActive)
            {
                _audioMixer.Seek(VideoPlayer.Position);
                _audioMixer.Play();
            }
        }

        // ───────────────────────── video stats strip (above the picture) ─────────────────────────

        // Fills the stats strip with what's known instantly (resolution from the MediaElement, size from the
        // clip record), then kicks off an async ffmpeg probe for the fps / codec / bitrate it can't read directly.
        private void ShowVideoStats()
        {
            int vw = VideoPlayer.NaturalVideoWidth, vh = VideoPlayer.NaturalVideoHeight;
            if (StatsResolution != null) StatsResolution.Text = (vw > 0 && vh > 0) ? $"{vw}×{vh}" : "-";
            if (StatsSize != null) StatsSize.Text = _activeClip != null ? FormatStatBytes(_activeClip.FileSizeBytes) : "-";
            if (StatsFps != null) StatsFps.Text = "…";
            if (StatsCodec != null) StatsCodec.Text = "…";
            if (StatsBitrate != null) StatsBitrate.Text = "…";

            string? path = _activeClip?.FilePath;
            if (!string.IsNullOrEmpty(path)) ProbeVideoStatsAsync(path!);
        }

        // Runs the bundled ffmpeg as "-i <file>" purely to read the stream dump it prints to stderr, parses the
        // codec / fps / bitrate out of it, and applies them - but only if the same clip is still on screen (the
        // user may have switched clips while the probe ran). Any failure degrades each field to a dash.
        private async void ProbeVideoStatsAsync(string path)
        {
            string ffmpeg = _app?.RecorderService?.FFmpegExePath ?? "";
            if (string.IsNullOrEmpty(ffmpeg) || !File.Exists(ffmpeg) || !File.Exists(path)) return;

            string dump = "";
            try
            {
                dump = await Task.Run(() =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpeg,
                        Arguments = $"-hide_banner -i \"{path}\"",
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var p = Process.Start(psi);
                    if (p == null) return "";
                    string err = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } }
                    return err;
                });
            }
            catch { return; }

            string codec = ParseStat(dump, @"Video:\s*([A-Za-z0-9]+)", MapCodec);
            string fps   = ParseStat(dump, @"([0-9]+(?:\.[0-9]+)?)\s*fps", FormatFps);
            string rate  = ParseStat(dump, @"bitrate:\s*([0-9]+)\s*kb/s", FormatBitrate);

            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.Invoke(() =>
            {
                if (_activeClip?.FilePath != path) return; // a different clip is showing now
                if (StatsFps != null) StatsFps.Text = fps;
                if (StatsCodec != null) StatsCodec.Text = codec;
                if (StatsBitrate != null) StatsBitrate.Text = rate;
            });
        }

        private static string ParseStat(string text, string pattern, Func<string, string> shape)
        {
            try
            {
                var m = Regex.Match(text, pattern);
                return m.Success ? shape(m.Groups[1].Value) : "-";
            }
            catch { return "-"; }
        }

        private static string MapCodec(string c) => c.ToLowerInvariant() switch
        {
            "h264" => "H.264",
            "hevc" or "h265" => "H.265",
            "av1"  => "AV1",
            "vp9"  => "VP9",
            "vp8"  => "VP8",
            "mpeg4" => "MPEG-4",
            _ => c.ToUpperInvariant(),
        };

        private static string FormatFps(string raw)
        {
            if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double f)) return raw;
            return Math.Abs(f - Math.Round(f)) < 0.05 ? $"{Math.Round(f):0}" : $"{f:0.0}"; // drop the ".0" on whole rates
        }

        private static string FormatBitrate(string kbpsRaw)
        {
            if (!int.TryParse(kbpsRaw, out int kbps)) return "-";
            return kbps >= 1000 ? $"{kbps / 1000.0:0.0} Mb/s" : $"{kbps} kb/s";
        }

        private static string FormatStatBytes(long bytes)
        {
            if (bytes <= 0) return "-";
            double mb = bytes / 1048576.0;
            return mb >= 1.0 ? $"{mb:0.0} MB" : $"{bytes / 1024.0:0} KB";
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            // Loop playback
            VideoPlayer.Position = TimeSpan.Zero;
            VideoPlayer.Play();
            if (_mixerActive)
            {
                _audioMixer.Seek(TimeSpan.Zero);
                _audioMixer.Play();
            }
        }

        private void PlayerTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isUserSeeking && VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                UpdateTimeText(VideoPlayer.Position, VideoPlayer.NaturalDuration.TimeSpan);
                double dur = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                UpdatePlayhead(dur > 0 ? VideoPlayer.Position.TotalSeconds / dur : 0);

                // A/V re-sync safeguard: if the live mixer drifted from the video or stopped
                // unexpectedly (e.g. right after a loop or a rapid seek), pull it back into line.
                if (_isPlaying && _mixerActive)
                {
                    if (!_audioMixer.IsOutputPlaying)
                    {
                        _audioMixer.Seek(VideoPlayer.Position);
                        _audioMixer.Play();
                    }
                    else if (Math.Abs((_audioMixer.CurrentTime - VideoPlayer.Position).TotalMilliseconds) > 300)
                    {
                        _audioMixer.Seek(VideoPlayer.Position);
                    }
                }
            }
        }

        private void UpdateTimeText(TimeSpan elapsed, TimeSpan total)
        {
            TimeStatusText.Text = $"{elapsed:mm\\:ss} / {total:mm\\:ss}";
        }

        // Player transport icons are vector Paths (defined in MainWindow.xaml) toggled by visibility,
        // replacing the previous emoji glyphs so they render crisply and consistently.
        private void SetPlayPauseIcon(bool playing)
        {
            if (PlayIcon == null || PauseIcon == null) return;
            PlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
            PauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetVolumeIcon(bool muted)
        {
            if (VolumeWaves == null || MuteCross == null) return;
            VolumeWaves.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
            MuteCross.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        }

        // ================= LIVE AUDIO MIXER =================
        // Loads the clip's separate system/mic tracks (if present) so the player can mix them live.
        private void SetupMixerForClip(Clip clip)
        {
            _audioMixer.Stop();
            _mixerActive = false;

            string sys = clip.SystemTrackPath ?? "";
            string mic = clip.MicTrackPath ?? "";
            string social = clip.SocialTrackPath ?? "";
            bool hasSeparate = (!string.IsNullOrEmpty(sys) && File.Exists(sys))
                            || (!string.IsNullOrEmpty(mic) && File.Exists(mic))
                            || (!string.IsNullOrEmpty(social) && File.Exists(social));

            if (hasSeparate && _audioMixer.Load(sys, mic, social))
            {
                _mixerActive = true;

                // Reset per-source controls to defaults without re-triggering the change handlers.
                _suppressMixEvents = true;
                SystemVolSlider.Value = 100; SystemVolText.Text = "100%"; SystemMuteToggle.IsChecked = false;
                MicVolSlider.Value = 100; MicVolText.Text = "100%"; MicMuteToggle.IsChecked = false;
                SocialVolSlider.Value = 100; SocialVolText.Text = "100%"; SocialMuteToggle.IsChecked = false;
                _suppressMixEvents = false;

                _audioMixer.SetSystemVolume(1f); _audioMixer.SetSystemMuted(false);
                _audioMixer.SetMicVolume(1f); _audioMixer.SetMicMuted(false);
                _audioMixer.SetSocialVolume(1f); _audioMixer.SetSocialMuted(false);

                // Rows are only interactive for sources that actually exist in this clip.
                SystemMixRow.IsEnabled = _audioMixer.HasSystem;
                SystemMixRow.Opacity = _audioMixer.HasSystem ? 1.0 : 0.4;
                MicMixRow.IsEnabled = _audioMixer.HasMic;
                MicMixRow.Opacity = _audioMixer.HasMic ? 1.0 : 0.4;
                SocialMixRow.IsEnabled = _audioMixer.HasSocial;
                SocialMixRow.Opacity = _audioMixer.HasSocial ? 1.0 : 0.4;
                MixUnavailableText.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Legacy clip (single mixed track): the video element plays its own audio.
                SystemMixRow.IsEnabled = false; SystemMixRow.Opacity = 0.4;
                MicMixRow.IsEnabled = false; MicMixRow.Opacity = 0.4;
                SocialMixRow.IsEnabled = false; SocialMixRow.Opacity = 0.4;
                MixUnavailableText.Visibility = Visibility.Visible;
            }

            ApplyMasterVolume();
        }

        private void MixerBtn_Click(object sender, RoutedEventArgs e)
        {
            MixerPopup.IsOpen = !MixerPopup.IsOpen;
        }

        private void SystemVolSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SystemVolText != null) SystemVolText.Text = $"{(int)SystemVolSlider.Value}%";
            if (_suppressMixEvents || !_mixerActive) return;
            _audioMixer.SetSystemVolume((float)(SystemVolSlider.Value / 100.0));
        }

        private void MicVolSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MicVolText != null) MicVolText.Text = $"{(int)MicVolSlider.Value}%";
            if (_suppressMixEvents || !_mixerActive) return;
            _audioMixer.SetMicVolume((float)(MicVolSlider.Value / 100.0));
        }

        private void SocialVolSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SocialVolText != null) SocialVolText.Text = $"{(int)SocialVolSlider.Value}%";
            if (_suppressMixEvents || !_mixerActive) return;
            _audioMixer.SetSocialVolume((float)(SocialVolSlider.Value / 100.0));
        }

        private void SystemMute_Click(object sender, RoutedEventArgs e)
        {
            if (_mixerActive) _audioMixer.SetSystemMuted(SystemMuteToggle.IsChecked == true);
        }

        private void MicMute_Click(object sender, RoutedEventArgs e)
        {
            if (_mixerActive) _audioMixer.SetMicMuted(MicMuteToggle.IsChecked == true);
        }

        private void SocialMute_Click(object sender, RoutedEventArgs e)
        {
            if (_mixerActive) _audioMixer.SetSocialMuted(SocialMuteToggle.IsChecked == true);
        }

        // ----- Right-click a waveform lane -> a small per-track volume popup -----

        // Right-clicking a lane opens a compact volume/mute control for just that track, positioned at the
        // cursor. The lane under the pointer is found from the click's Y over the equal-height lanes. The
        // popup is a second face on the same mixer state, so it drives the matching main-mixer row (single
        // source of truth) instead of touching the mixer directly for volume.
        private void WaveformCanvas_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // don't let the right-click bubble to any parent context menu
            if (!_mixerActive || _laneData.Count == 0) return;

            double h = WaveformCanvas.ActualHeight;
            if (h <= 0) return;

            int laneCount = _laneData.Count;
            double y = e.GetPosition(WaveformCanvas).Y;
            int idx = Math.Clamp((int)(y / (h / laneCount)), 0, laneCount - 1);
            var lane = _laneData[idx];

            // Only the real per-source lanes are mixable; a legacy single combined lane (Label "") isn't.
            bool exists = lane.Label switch
            {
                "MIC" => _audioMixer.HasMic,
                "SYSTEM" => _audioMixer.HasSystem,
                "SOCIAL" => _audioMixer.HasSocial,
                _ => false
            };
            if (!exists) return;

            OpenLaneVolumePopup(lane.Label, lane.Color);
        }

        private void OpenLaneVolumePopup(string source, Color laneColor)
        {
            _activeLaneSource = source;
            var brush = new SolidColorBrush(laneColor);

            // Mirror the current state from the matching main-mixer row.
            (double vol, bool muted, string name) = source switch
            {
                "MIC" => (MicVolSlider.Value, MicMuteToggle.IsChecked == true, "MICROPHONE"),
                "SYSTEM" => (SystemVolSlider.Value, SystemMuteToggle.IsChecked == true, "SYSTEM / GAME"),
                "SOCIAL" => (SocialVolSlider.Value, SocialMuteToggle.IsChecked == true, "SOCIAL APP"),
                _ => (100.0, false, "")
            };

            // Tint the popup's controls to the lane hue.
            LanePopupLabel.Text = name;
            LanePopupLabel.Foreground = brush;
            LanePopupSlider.Foreground = brush;
            LanePopupMuteToggle.Foreground = brush;

            _suppressLaneEvents = true;
            LanePopupSlider.Value = vol;
            LanePopupMuteToggle.IsChecked = muted;
            _suppressLaneEvents = false;
            LanePopupVolText.Text = $"{(int)vol}%";

            LaneVolumePopup.IsOpen = true;
        }

        private void LanePopupSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (LanePopupVolText != null) LanePopupVolText.Text = $"{(int)LanePopupSlider.Value}%";
            if (_suppressLaneEvents) return;

            // Drive the matching main-mixer slider; its handler applies the volume to the live mixer and
            // keeps the two popups consistent.
            switch (_activeLaneSource)
            {
                case "MIC": MicVolSlider.Value = LanePopupSlider.Value; break;
                case "SYSTEM": SystemVolSlider.Value = LanePopupSlider.Value; break;
                case "SOCIAL": SocialVolSlider.Value = LanePopupSlider.Value; break;
            }
        }

        private void LanePopupMute_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressLaneEvents) return;
            bool muted = LanePopupMuteToggle.IsChecked == true;

            // Mirror onto the matching main-mixer toggle and the live mixer (programmatic IsChecked does
            // not re-raise that toggle's Click, so apply the mute here).
            switch (_activeLaneSource)
            {
                case "MIC": MicMuteToggle.IsChecked = muted; if (_mixerActive) _audioMixer.SetMicMuted(muted); break;
                case "SYSTEM": SystemMuteToggle.IsChecked = muted; if (_mixerActive) _audioMixer.SetSystemMuted(muted); break;
                case "SOCIAL": SocialMuteToggle.IsChecked = muted; if (_mixerActive) _audioMixer.SetSocialMuted(muted); break;
            }
        }

        // ================= WAVEFORM (3 stacked per-source lanes) =================

        // One drawable lane: peaks + its color identity + the bars currently on the canvas. Played and
        // Unplayed brushes are cached at draw time so the playhead can recolor cheaply each tick.
        private sealed class WaveLane
        {
            public float[] Peaks = Array.Empty<float>();
            public string Label = "";
            public Color Color;
            public SolidColorBrush? Played;
            public SolidColorBrush? Unplayed;
            public readonly List<System.Windows.Shapes.Rectangle> Bars = new();
        }

        // Lane colors. System follows the user's chosen accent; mic is warm amber; social is the Wisp
        // brand purple. A clip with no separate tracks falls back to one accent-colored combined lane.
        private static Color LaneSystemColor => ThemeManager.AccentColor;
        private static readonly Color LaneMicColor = Color.FromRgb(0xFF, 0xB4, 0x54);
        private static readonly Color LaneSocialColor = Color.FromRgb(0xB9, 0x8C, 0xFF);

        // Computes a peaks array per available source off the UI thread, then draws all lanes. Sidecar
        // tracks (mic/system/social) are preferred; legacy/imported clips fall back to one combined lane
        // computed from the MP4's own audio, so scrubbing + trim keep working everywhere.
        private void LoadWaveform(Clip clip)
        {
            _laneData.Clear();
            WaveformCanvas.Children.Clear();
            _playhead = null;

            int token = ++_waveformToken;
            string micPath = clip.MicTrackPath ?? "";
            string sysPath = clip.SystemTrackPath ?? "";
            string socialPath = clip.SocialTrackPath ?? "";
            string combinedPath = clip.FilePath ?? "";

            System.Threading.Tasks.Task.Run(() =>
            {
                var lanes = new List<WaveLane>();
                AddLaneIfPresent(lanes, micPath, "MIC", LaneMicColor);
                AddLaneIfPresent(lanes, sysPath, "SYSTEM", LaneSystemColor);
                AddLaneIfPresent(lanes, socialPath, "SOCIAL", LaneSocialColor);

                if (lanes.Count == 0)
                {
                    // Legacy / imported clip: a single combined lane from the clip's own audio track.
                    var peaks = WaveformGenerator.ComputePeaks(combinedPath, 400);
                    if (peaks != null)
                        lanes.Add(new WaveLane { Peaks = peaks, Label = "", Color = LaneSystemColor });
                }

                Dispatcher.Invoke(() =>
                {
                    if (token != _waveformToken) return; // a newer clip was opened meanwhile
                    _laneData.Clear();
                    _laneData.AddRange(lanes);
                    DrawWaveform();
                });
            });
        }

        private static void AddLaneIfPresent(List<WaveLane> lanes, string path, string label, Color color)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var peaks = WaveformGenerator.ComputePeaks(path, 400);
            if (peaks != null)
                lanes.Add(new WaveLane { Peaks = peaks, Label = label, Color = color });
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b, double opacity)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b)) { Opacity = opacity };
            br.Freeze();
            return br;
        }

        private static SolidColorBrush Frozen(Color c, double opacity)
        {
            var br = new SolidColorBrush(c) { Opacity = opacity };
            br.Freeze();
            return br;
        }

        // Visuals (lines + numbered flags) overlaid on the waveform for a chained clip's moment markers.
        // Tracked so they can be cleared independently of the waveform bars on redraw / clip change.
        private readonly List<UIElement> _chainMarkerVisuals = new();

        // Same idea for kill-detection markers - their own lane so a clip can show chain moments AND
        // kills at once (kills draw in a fixed red, independent of the user's accent).
        private readonly List<UIElement> _killMarkerVisuals = new();

        private void DrawWaveform()
        {
            WaveformCanvas.Children.Clear();
            _chainMarkerVisuals.Clear(); // children were just cleared; drop stale references too
            _killMarkerVisuals.Clear();
            foreach (var lane in _laneData) lane.Bars.Clear();
            _playhead = null;

            double w = WaveformCanvas.ActualWidth;
            double h = WaveformCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            int laneCount = _laneData.Count;
            if (laneCount > 0)
            {
                double laneH = h / laneCount;
                for (int li = 0; li < laneCount; li++)
                {
                    var lane = _laneData[li];
                    lane.Played = Frozen(lane.Color, 0.95);    // already played
                    lane.Unplayed = Frozen(lane.Color, 0.28);  // not yet played (same hue, dim)

                    double laneTop = li * laneH;
                    double mid = laneTop + laneH / 2.0;
                    double maxBar = laneH * 0.72; // padding keeps adjacent lanes from touching

                    var peaks = lane.Peaks;
                    if (peaks != null && peaks.Length > 0)
                    {
                        int n = peaks.Length;
                        double barW = w / n;
                        for (int i = 0; i < n; i++)
                        {
                            double bh = Math.Max(1.0, peaks[i] * maxBar);
                            var rect = new System.Windows.Shapes.Rectangle
                            {
                                Width = Math.Max(1.0, barW - 1.0),
                                Height = bh,
                                Fill = lane.Unplayed
                            };
                            Canvas.SetLeft(rect, i * barW);
                            Canvas.SetTop(rect, mid - bh / 2.0);
                            WaveformCanvas.Children.Add(rect);
                            lane.Bars.Add(rect);
                        }
                    }

                    // Subtle source label at the lane's left edge.
                    if (!string.IsNullOrEmpty(lane.Label))
                    {
                        var tb = new TextBlock
                        {
                            Text = lane.Label,
                            FontSize = 8,
                            FontWeight = FontWeights.Bold,
                            FontFamily = new FontFamily("Consolas"),
                            Foreground = Frozen(lane.Color, 0.8),
                            IsHitTestVisible = false
                        };
                        Canvas.SetLeft(tb, 6);
                        Canvas.SetTop(tb, laneTop + 2);
                        WaveformCanvas.Children.Add(tb);
                    }
                }
            }

            // Thin playhead on top spanning all lanes.
            _playhead = new System.Windows.Shapes.Rectangle
            {
                Width = 2,
                Height = h,
                Fill = Frozen(0xE7, 0xE7, 0xEA, 1.0)
            };
            Canvas.SetTop(_playhead, 0);
            WaveformCanvas.Children.Add(_playhead);

            double dur = VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds : 0;
            UpdatePlayhead(dur > 0 ? VideoPlayer.Position.TotalSeconds / dur : 0);
            UpdateTrimUI();
            DrawChainMarkers();
            DrawKillMarkers();
            DrawPluginMarkers();
        }

        // Overlays a marker on the waveform/scrubber at each chained moment, so the player shows not just
        // THAT a clip is chained but WHERE each tap landed. Each marker is a thin accent line plus a small
        // numbered flag you can click to jump straight to that moment. No-op for ordinary clips.
        private void DrawChainMarkers()
        {
            foreach (var v in _chainMarkerVisuals) WaveformCanvas.Children.Remove(v);
            _chainMarkerVisuals.Clear();

            var clip = _activeClip;
            if (clip == null || !clip.IsChained) return;

            double w = WaveformCanvas.ActualWidth;
            double h = WaveformCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double dur = VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds : 0;
            if (dur <= 0) return;

            var offsets = clip.ChainMarkerOffsets;
            Color accent = ThemeManager.AccentColor;
            var lineBrush = Frozen(accent, 0.85);
            var flagBrush = new SolidColorBrush(accent);
            flagBrush.Freeze();

            for (int i = 0; i < offsets.Count; i++)
            {
                double frac = Math.Clamp(offsets[i] / dur, 0.0, 1.0);
                double x = frac * w;

                // Full-height marker line (click passes through to the flag / waveform underneath).
                var line = new System.Windows.Shapes.Rectangle
                {
                    Width = 2,
                    Height = h,
                    Fill = lineBrush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(line, x - 1);
                Canvas.SetTop(line, 0);
                WaveformCanvas.Children.Add(line);
                _chainMarkerVisuals.Add(line);

                // Numbered flag at the top - click to jump to this moment.
                var flag = new Border
                {
                    Background = flagBrush,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 0, 4, 1),
                    Cursor = Cursors.Hand,
                    Tag = offsets[i],
                    ToolTip = $"Moment {i + 1} - jump here",
                    Child = new TextBlock
                    {
                        Text = (i + 1).ToString(),
                        Foreground = Frozen(0x0A, 0x14, 0x14, 1.0),
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Consolas")
                    }
                };
                flag.MouseLeftButtonDown += ChainMarker_Click;
                // Center the flag on the line (its width isn't measured yet, so nudge by a typical half-width).
                Canvas.SetLeft(flag, Math.Max(0, x - 7));
                Canvas.SetTop(flag, 1);
                WaveformCanvas.Children.Add(flag);
                _chainMarkerVisuals.Add(flag);
            }
        }

        // Overlays a marker at each detected kill, mirroring the chain markers above but in a FIXED red
        // (independent of the user's accent) with a "K" flag so the two lanes read differently at a
        // glance. When the clip is also chained, kill flags drop below the chain flags so both rows
        // stay clickable. No-op for clips without kill data.
        private void DrawKillMarkers()
        {
            foreach (var v in _killMarkerVisuals) WaveformCanvas.Children.Remove(v);
            _killMarkerVisuals.Clear();

            var clip = _activeClip;
            if (clip == null || !clip.HasKills) return;

            double w = WaveformCanvas.ActualWidth;
            double h = WaveformCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double dur = VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds : 0;
            if (dur <= 0) return;

            var offsets = clip.KillMarkerOffsets;
            var lineBrush = Frozen(0xFF, 0x45, 0x55, 0.9);
            var flagBrush = Frozen(0xFF, 0x45, 0x55, 1.0);
            double flagTop = clip.IsChained ? 19 : 1; // second row when chain flags occupy the top

            for (int i = 0; i < offsets.Count; i++)
            {
                double frac = Math.Clamp(offsets[i] / dur, 0.0, 1.0);
                double x = frac * w;

                // Full-height marker line (click passes through to the flag / waveform underneath).
                var line = new System.Windows.Shapes.Rectangle
                {
                    Width = 2,
                    Height = h,
                    Fill = lineBrush,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(line, x - 1);
                Canvas.SetTop(line, 0);
                WaveformCanvas.Children.Add(line);
                _killMarkerVisuals.Add(line);

                // "K" flag - click to jump to this kill (same seek handler as chain flags; it only
                // reads the offset from Tag).
                var flag = new Border
                {
                    Background = flagBrush,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 0, 4, 1),
                    Cursor = Cursors.Hand,
                    Tag = offsets[i],
                    ToolTip = $"Kill {i + 1} - jump here",
                    Child = new TextBlock
                    {
                        Text = "K",
                        Foreground = Frozen(0x0A, 0x14, 0x14, 1.0),
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Consolas")
                    }
                };
                flag.MouseLeftButtonDown += ChainMarker_Click;
                Canvas.SetLeft(flag, Math.Max(0, x - 7));
                Canvas.SetTop(flag, flagTop);
                WaveformCanvas.Children.Add(flag);
                _killMarkerVisuals.Add(flag);
            }
        }

        private void ChainMarker_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // jump straight here; don't start a waveform scrub
            if (sender is FrameworkElement fe && fe.Tag is double offset && VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                double dur = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                var pos = TimeSpan.FromSeconds(Math.Clamp(offset, 0, dur));
                VideoPlayer.Position = pos;
                if (_mixerActive) _audioMixer.Seek(pos);
                UpdatePlayhead(dur > 0 ? pos.TotalSeconds / dur : 0);
                UpdateTimeText(pos, VideoPlayer.NaturalDuration.TimeSpan);
            }
        }

        // Moves the playhead and recolors bars left of it as "played" across every lane (the strip is
        // also the scrubber, so x maps to time over the full width regardless of lane count).
        private void UpdatePlayhead(double fraction)
        {
            double w = WaveformCanvas.ActualWidth;
            if (w <= 0) return;
            fraction = Math.Clamp(fraction, 0.0, 1.0);

            if (_playhead != null)
                Canvas.SetLeft(_playhead, (fraction * w) - 1);

            foreach (var lane in _laneData)
            {
                int n = lane.Bars.Count;
                if (n == 0 || lane.Played == null || lane.Unplayed == null) continue;
                for (int i = 0; i < n; i++)
                {
                    var target = ((i + 0.5) / n) <= fraction ? lane.Played : lane.Unplayed;
                    if (!ReferenceEquals(lane.Bars[i].Fill, target))
                        lane.Bars[i].Fill = target;
                }
            }
        }

        private void WaveformCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawWaveform();
            // Keep the trim clamps pinned to their boundaries when the waveform gets (or changes) width -
            // including the first real layout right after the export panel opens, which is when the right
            // clamp previously missed its position.
            if (_isExportSidebarOpen) UpdateTrimUI();
        }

        // The waveform doubles as the scrubber: press and drag left/right to seek. We pause playback
        // for the duration of the drag (ScrubbingEnabled shows frames as you move) and throttle the
        // actual seeks, then resume on release - rapid seeking while playing was the source of the lag.
        private void WaveformCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!VideoPlayer.NaturalDuration.HasTimeSpan) return;

            _isUserSeeking = true;
            _scrubWasPlaying = _isPlaying;
            _lastScrubTicks = 0;
            WaveformCanvas.CaptureMouse();

            if (_isPlaying)
            {
                VideoPlayer.Pause();
                if (_mixerActive) _audioMixer.Pause();
                _isPlaying = false;
                SetPlayPauseIcon(false);
            }

            CommitScrub(e.GetPosition(WaveformCanvas).X);
        }

        private void WaveformCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isUserSeeking || e.LeftButton != MouseButtonState.Pressed) return;

            double x = e.GetPosition(WaveformCanvas).X;
            PreviewScrub(x); // cheap visual feedback every move

            long now = Environment.TickCount64;
            if (now - _lastScrubTicks >= 80) // throttle the heavier video/audio seek
            {
                _lastScrubTicks = now;
                CommitScrub(x);
            }
        }

        private void WaveformCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isUserSeeking) return;

            WaveformCanvas.ReleaseMouseCapture();
            CommitScrub(e.GetPosition(WaveformCanvas).X); // final precise seek
            _isUserSeeking = false;

            if (_scrubWasPlaying)
            {
                if (_mixerActive) { _audioMixer.Seek(VideoPlayer.Position); _audioMixer.Play(); }
                VideoPlayer.Play();
                _isPlaying = true;
                SetPlayPauseIcon(true);
            }
        }

        // Visual only: move the playhead and update the time text without touching the decoders.
        private void PreviewScrub(double x)
        {
            double w = WaveformCanvas.ActualWidth;
            if (w <= 0 || !VideoPlayer.NaturalDuration.HasTimeSpan) return;
            double frac = Math.Clamp(x / w, 0.0, 1.0);
            UpdatePlayhead(frac);
            UpdateTimeText(TimeSpan.FromSeconds(frac * VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds), VideoPlayer.NaturalDuration.TimeSpan);
        }

        // Actually seeks the video (and audio mixer) to the given x.
        private void CommitScrub(double x)
        {
            double w = WaveformCanvas.ActualWidth;
            if (w <= 0 || !VideoPlayer.NaturalDuration.HasTimeSpan) return;
            double frac = Math.Clamp(x / w, 0.0, 1.0);
            var pos = TimeSpan.FromSeconds(frac * VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds);
            VideoPlayer.Position = pos;
            if (_mixerActive) _audioMixer.Seek(pos);
            UpdatePlayhead(frac);
            UpdateTimeText(pos, VideoPlayer.NaturalDuration.TimeSpan);
        }

        private void PlayerTagsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_activeClip == null || PlayerTagsBtn == null) return;

            var menu = new ContextMenu();
            try
            {
                var allTags = _app.DbService.GetAllTagDefinitions();
                var clipTags = (_activeClip.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                 .Select(t => t.Trim())
                                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var tag in allTags)
                {
                    var item = new MenuItem
                    {
                        Header = tag.Name,
                        IsCheckable = true,
                        IsChecked = clipTags.Contains(tag.Name)
                    };
                    var tagName = tag.Name;
                    item.Click += (s, ev) =>
                    {
                        ToggleTagOnClip(_activeClip, tagName);
                    };
                    menu.Items.Add(item);
                }

                if (allTags.Count > 0)
                {
                    menu.Items.Add(new Separator());
                }

                var manageItem = new MenuItem { Header = "Manage tags..." };
                manageItem.Click += (s, ev) =>
                {
                    OpenManageTagsDialog();
                };
                menu.Items.Add(manageItem);

                menu.PlacementTarget = PlayerTagsBtn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to build Player Tags menu: {ex.Message}");
            }
        }

        /// <summary>Builds a Segoe MDL2 Assets glyph for a code-built MenuItem.Icon, so the player's
        /// right-click menu uses the same crisp vector icons as the rest of Wisp (no emoji).</summary>
        private static TextBlock MenuGlyph(string glyph, string colorHex = "#B9CACB")
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = (Brush)new BrushConverter().ConvertFromString(colorHex)!
            };
        }

        private void VideoCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_activeClip == null) return;

            var menu = new ContextMenu();
            try
            {
                // 1. Assign Tags submenu
                var assignItem = new MenuItem { Header = "Assign tags", Icon = MenuGlyph("") };
                
                var allTags = _app.DbService.GetAllTagDefinitions();
                var clipTags = (_activeClip.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                 .Select(t => t.Trim())
                                                 .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var tag in allTags)
                {
                    var item = new MenuItem
                    {
                        Header = tag.Name,
                        IsCheckable = true,
                        IsChecked = clipTags.Contains(tag.Name)
                    };
                    var tagName = tag.Name;
                    item.Click += (s, ev) =>
                    {
                        ToggleTagOnClip(_activeClip, tagName);
                    };
                    assignItem.Items.Add(item);
                }

                if (allTags.Count > 0)
                {
                    assignItem.Items.Add(new Separator());
                }

                var manageItem = new MenuItem { Header = "Manage tags..." };
                manageItem.Click += (s, ev) =>
                {
                    OpenManageTagsDialog();
                };
                assignItem.Items.Add(manageItem);
                
                menu.Items.Add(assignItem);
                menu.Items.Add(new Separator());

                // 2. Play / Pause
                var playPauseItem = new MenuItem { Header = _isPlaying ? "Pause" : "Play", Icon = MenuGlyph(_isPlaying ? "" : "") };
                playPauseItem.Click += (s, ev) => TogglePlayPause();
                menu.Items.Add(playPauseItem);

                // 3. Mute / Unmute
                var muteItem = new MenuItem { Header = _isMuted ? "Unmute" : "Mute", Icon = MenuGlyph(_isMuted ? "" : "") };
                muteItem.Click += (s, ev) => MuteBtn_Click(sender, ev);
                menu.Items.Add(muteItem);

                menu.Items.Add(new Separator());

                // 4. Close Player
                var closeItem = new MenuItem { Header = "Close player", Icon = MenuGlyph("", "#FF6B6B") };
                closeItem.Click += (s, ev) => ClosePlayer();
                menu.Items.Add(closeItem);

                menu.IsOpen = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to build Player ContextMenu: {ex.Message}");
            }
        }
    }
}
