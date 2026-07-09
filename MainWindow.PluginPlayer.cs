using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Wisp.Models;
using Wisp.Plugins.Player;
using Wisp.Services;

namespace Wisp
{
    /// <summary>
    /// The host side of the plugin Player surface (<see cref="Wisp.Plugins.Player.IWispPlayer"/>): the
    /// read accessors App marshals plugin queries to, the seek/transport shims, and the rendering of
    /// plugin-contributed control-bar buttons and timeline markers onto the existing player. Marker visuals
    /// are tracked separately from chain markers so the two systems never clobber each other on redraw.
    /// </summary>
    public partial class MainWindow : Window
    {
        // Plugin timeline-marker visuals currently on the waveform (lines + flags), tracked so they can be
        // cleared/redrawn independently of the chain markers and the waveform bars.
        private readonly List<UIElement> _pluginMarkerVisuals = new();

        // ───────────────────────── read accessors (called on the UI thread via App) ─────────────────────────

        internal bool PlayerIsOpen => _activeClip != null;
        internal Clip? PlayerActiveClip => _activeClip;
        internal double PlayerPositionSeconds => VideoPlayer?.Position.TotalSeconds ?? 0.0;
        internal double PlayerDurationSeconds =>
            VideoPlayer != null && VideoPlayer.NaturalDuration.HasTimeSpan
                ? VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds : 0.0;
        internal bool PlayerIsPlaying => _isPlaying;

        // ───────────────────────── transport shims ─────────────────────────

        /// <summary>Seeks the open clip to <paramref name="seconds"/> (clamped). Mirrors the chain-marker jump.</summary>
        internal void SeekToSeconds(double seconds)
        {
            if (VideoPlayer == null || _activeClip == null || !VideoPlayer.NaturalDuration.HasTimeSpan) return;
            double dur = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            var pos = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, dur));
            VideoPlayer.Position = pos;
            if (_mixerActive) _audioMixer.Seek(pos);
            UpdatePlayhead(dur > 0 ? pos.TotalSeconds / dur : 0);
            UpdateTimeText(pos, VideoPlayer.NaturalDuration.TimeSpan);
        }

        internal void PlayerPlay() { if (_activeClip != null && !_isPlaying) TogglePlayPause(); }
        internal void PlayerPause() { if (_activeClip != null && _isPlaying) TogglePlayPause(); }

        // ───────────────────────── control-bar buttons ─────────────────────────

        /// <summary>Rebuilds the plugin button strip on the player control bar from App's registry.</summary>
        internal void RefreshPlayerPluginButtons()
        {
            if (PlayerPluginBar == null) return;
            PlayerPluginBar.Children.Clear();
            foreach (var entry in _app.GetPlayerButtons())
                PlayerPluginBar.Children.Add(BuildPlayerButton(entry));
        }

        private Button BuildPlayerButton(PluginPlayerButtonEntry entry)
        {
            var def = entry.Button;
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            string glyph = Services.Plugins.GlyphUtil.ToGlyph(def.IconGlyph);
            if (!string.IsNullOrEmpty(glyph))
            {
                var icon = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, string.IsNullOrEmpty(def.Label) ? 0 : 7, 0)
                };
                icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                content.Children.Add(icon);
            }
            if (!string.IsNullOrEmpty(def.Label))
            {
                content.Children.Add(new TextBlock
                {
                    Text = def.Label,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE2))
                });
            }

            var btn = new Button
            {
                Style = (Style)FindResource("PlayerPluginButtonStyle"),
                Content = content,
                ToolTip = string.IsNullOrEmpty(def.Tooltip) ? null : def.Tooltip
            };

            string pid = entry.PluginId;
            string bid = def.Id;
            var onClick = def.OnClick;
            btn.Click += (s, e) =>
            {
                try { onClick(); }
                catch (Exception ex)
                {
                    Logger.Error($"[Plugin:{pid}] player button '{bid}' threw.", ex);
                    ShowToast("Plugin error", $"A plugin action failed: {ex.Message}", ToastKind.Error);
                }
            };
            return btn;
        }

        // ───────────────────────── timeline markers ─────────────────────────

        /// <summary>Redraws plugin timeline markers (called when a plugin sets/clears them at runtime).</summary>
        internal void RefreshPlayerMarkers() => DrawPluginMarkers();

        /// <summary>
        /// Paints each plugin marker as a coloured line plus a small clickable flag near the bottom of the
        /// scrubber (chain flags sit at the top, so the two never overlap). Left-click seeks to the marker;
        /// right-click runs the marker's optional action (e.g. remove). Mirrors <c>DrawChainMarkers</c>, and
        /// is called from the same places so markers track the waveform on open, resize, and media-open.
        /// </summary>
        private void DrawPluginMarkers()
        {
            foreach (var v in _pluginMarkerVisuals) WaveformCanvas.Children.Remove(v);
            _pluginMarkerVisuals.Clear();

            if (_activeClip == null) return;
            var markers = _app.GetPlayerMarkers();
            if (markers.Count == 0) return;

            double w = WaveformCanvas.ActualWidth;
            double h = WaveformCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double dur = VideoPlayer.NaturalDuration.HasTimeSpan ? VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds : 0;
            if (dur <= 0) return;

            Color accent = ThemeManager.AccentColor;

            foreach (var entry in markers)
            {
                var m = entry.Marker;
                double frac = Math.Clamp(m.PositionSeconds / dur, 0.0, 1.0);
                double x = frac * w;

                Color c = ParseColorOr(m.ColorHex, accent);
                var lineBrush = new SolidColorBrush(c) { Opacity = 0.8 }; lineBrush.Freeze();
                var flagBrush = new SolidColorBrush(c); flagBrush.Freeze();

                // Full-height line (clicks pass through to the flag / waveform underneath).
                var line = new Rectangle { Width = 2, Height = h, Fill = lineBrush, IsHitTestVisible = false };
                Canvas.SetLeft(line, x - 1);
                Canvas.SetTop(line, 0);
                WaveformCanvas.Children.Add(line);
                _pluginMarkerVisuals.Add(line);

                // Clickable flag at the BOTTOM of the strip.
                var flag = new Border
                {
                    Background = flagBrush,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 0, 4, 1),
                    Cursor = Cursors.Hand,
                    ToolTip = string.IsNullOrEmpty(m.Tooltip) ? null : m.Tooltip,
                    Child = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(m.Label) ? "◆" : m.Label, // ◆ when unlabeled
                        Foreground = Frozen(0x0A, 0x14, 0x14, 1.0),
                        FontSize = 9,
                        FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Consolas")
                    }
                };

                double pos = m.PositionSeconds;
                var onRight = m.OnRightClick;
                string pid = entry.PluginId;
                flag.MouseLeftButtonDown += (s, e) => { e.Handled = true; SeekToSeconds(pos); };
                if (onRight != null)
                {
                    flag.MouseRightButtonUp += (s, e) =>
                    {
                        e.Handled = true; // don't fall through to the lane-volume popup
                        try { onRight(); }
                        catch (Exception ex) { Logger.Error($"[Plugin:{pid}] marker right-click threw.", ex); }
                    };
                }

                Canvas.SetLeft(flag, Math.Max(0, x - 7));
                Canvas.SetTop(flag, Math.Max(0, h - 17));
                WaveformCanvas.Children.Add(flag);
                _pluginMarkerVisuals.Add(flag);
            }
        }

        private static Color ParseColorOr(string? hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            try { return (Color)ColorConverter.ConvertFromString(hex.Trim()); }
            catch { return fallback; }
        }

        // ───────────────────────── video overlays ─────────────────────────
        // Plugin-contributed visual layers hosted over the player video (PiP webcam, HUDs, …). Geometry is
        // normalised to the displayed (letterbox-corrected) video rect, so overlays scale with the window and
        // the very same rectangle can drive export burn-in. We REBUILD on add/remove and only REPOSITION on
        // resize / media-open, so a hosted MediaElement is never reparented mid-play (which would reset it).

        private sealed class OverlayVisual
        {
            public Grid Container = null!;
            public PlayerOverlay Overlay = null!;
            public PlayerOverlayRect Rect; // current normalised rect (updated as the user drags/resizes)
        }

        private readonly List<OverlayVisual> _pluginOverlayVisuals = new();

        /// <summary>Rebuilds the plugin overlay layer from App's registry (called on add/remove/clear).</summary>
        internal void RefreshPlayerOverlays()
        {
            if (PlayerOverlayHost == null) return;

            // Tear down old containers, detaching plugin content so it can be re-hosted elsewhere without an
            // "element already has a parent" error.
            foreach (var v in _pluginOverlayVisuals)
            {
                DetachOverlayContent(v.Overlay);
                PlayerOverlayHost.Children.Remove(v.Container);
            }
            _pluginOverlayVisuals.Clear();

            if (_activeClip == null) return;
            var overlays = _app.GetPlayerOverlays();
            if (overlays.Count == 0) return;

            var (vx, vy, vw, vh) = ComputeVideoRect();

            foreach (var entry in overlays)
            {
                var ov = entry.Overlay;
                if (ov.Content is not FrameworkElement content)
                {
                    Logger.Warn($"[Plugin:{entry.PluginId}] player overlay '{ov.Id}' Content is not a WPF element; ignored.");
                    continue;
                }
                DetachFromParent(content);
                content.HorizontalAlignment = HorizontalAlignment.Stretch;
                content.VerticalAlignment = VerticalAlignment.Stretch;

                // Transparent (not null) background so the whole container is hit-testable → draggable even
                // over letterboxed/empty regions of the hosted content.
                var container = new Grid { Opacity = Math.Clamp(ov.Opacity, 0.0, 1.0), Background = Brushes.Transparent };
                container.Children.Add(content);
                Panel.SetZIndex(container, ov.ZIndex);

                var visual = new OverlayVisual
                {
                    Container = container,
                    Overlay = ov,
                    Rect = new PlayerOverlayRect(ov.X, ov.Y, ov.Width, ov.Height)
                };

                if (ov.Movable)
                {
                    container.Cursor = Cursors.SizeAll;
                    WireOverlayDrag(visual);
                }
                if (ov.Resizable) AddOverlayResizeGrip(visual);

                PlaceOverlay(visual, vx, vy, vw, vh);
                PlayerOverlayHost.Children.Add(container);
                _pluginOverlayVisuals.Add(visual);
            }
        }

        /// <summary>Repositions existing overlays without a rebuild (called on resize / media-open).</summary>
        private void RepositionPlayerOverlays()
        {
            if (_pluginOverlayVisuals.Count == 0) return;
            var (vx, vy, vw, vh) = ComputeVideoRect();
            foreach (var v in _pluginOverlayVisuals) PlaceOverlay(v, vx, vy, vw, vh);
        }

        /// <summary>Clears every plugin overlay visual (defensive teardown on player close).</summary>
        private void ClearPlayerOverlayVisuals()
        {
            foreach (var v in _pluginOverlayVisuals) DetachOverlayContent(v.Overlay);
            _pluginOverlayVisuals.Clear();
            PlayerOverlayHost?.Children.Clear();
        }

        private void VideoPlayerGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RepositionPlayerOverlays();
            LayoutCropOverlay(); // keeps the 9:16 crop chooser glued to the letterboxed video (no-op unless vertical)
        }

        // Map a visual's normalised rect onto the live video rectangle (pixels within the overlay canvas).
        private static void PlaceOverlay(OverlayVisual v, double vx, double vy, double vw, double vh)
        {
            if (vw <= 0 || vh <= 0) return;
            double w = Math.Max(16, v.Rect.Width * vw);
            double h = Math.Max(16, v.Rect.Height * vh);
            double x = Math.Clamp(vx + v.Rect.X * vw, vx, Math.Max(vx, vx + vw - w));
            double y = Math.Clamp(vy + v.Rect.Y * vh, vy, Math.Max(vy, vy + vh - h));
            v.Container.Width = w;
            v.Container.Height = h;
            Canvas.SetLeft(v.Container, x);
            Canvas.SetTop(v.Container, y);
        }

        /// <summary>The displayed (letterbox-corrected) video rectangle inside VideoPlayerGrid, in px.</summary>
        private (double X, double Y, double W, double H) ComputeVideoRect()
        {
            double gw = VideoPlayerGrid?.ActualWidth ?? 0, gh = VideoPlayerGrid?.ActualHeight ?? 0;
            double nvw = VideoPlayer?.NaturalVideoWidth ?? 0, nvh = VideoPlayer?.NaturalVideoHeight ?? 0;
            if (gw <= 0 || gh <= 0) return (0, 0, 0, 0);
            if (nvw <= 0 || nvh <= 0) return (0, 0, gw, gh); // natural size unknown yet → use the full grid
            double scale = Math.Min(gw / nvw, gh / nvh);
            double dw = nvw * scale, dh = nvh * scale;
            return ((gw - dw) / 2.0, (gh - dh) / 2.0, dw, dh);
        }

        private void WireOverlayDrag(OverlayVisual v)
        {
            var container = v.Container;
            bool dragging = false;
            Point start = default;
            double startLeft = 0, startTop = 0;

            container.MouseLeftButtonDown += (s, e) =>
            {
                dragging = true;
                start = e.GetPosition(PlayerOverlayHost);
                startLeft = Canvas.GetLeft(container);
                startTop = Canvas.GetTop(container);
                container.CaptureMouse();
                e.Handled = true; // don't toggle play/pause on the video underneath
            };
            container.MouseMove += (s, e) =>
            {
                if (!dragging) return;
                var (vx, vy, vw, vh) = ComputeVideoRect();
                var pos = e.GetPosition(PlayerOverlayHost);
                double nl = Math.Clamp(startLeft + (pos.X - start.X), vx, Math.Max(vx, vx + vw - container.Width));
                double nt = Math.Clamp(startTop + (pos.Y - start.Y), vy, Math.Max(vy, vy + vh - container.Height));
                Canvas.SetLeft(container, nl);
                Canvas.SetTop(container, nt);
            };
            container.MouseLeftButtonUp += (s, e) =>
            {
                if (!dragging) return;
                dragging = false;
                container.ReleaseMouseCapture();
                e.Handled = true;
                CommitOverlayRect(v);
            };
        }

        private void AddOverlayResizeGrip(OverlayVisual v)
        {
            var grip = new Rectangle
            {
                Width = 14,
                Height = 14,
                RadiusX = 2,
                RadiusY = 2,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 1, 1),
                Cursor = Cursors.SizeNWSE,
                Opacity = 0.85
            };
            grip.SetResourceReference(Shape.FillProperty, "AccentBrush");

            bool sizing = false;
            Point start = default;
            double startW = 0, startH = 0;

            grip.MouseLeftButtonDown += (s, e) =>
            {
                sizing = true;
                start = e.GetPosition(PlayerOverlayHost);
                startW = v.Container.Width;
                startH = v.Container.Height;
                grip.CaptureMouse();
                e.Handled = true;
            };
            grip.MouseMove += (s, e) =>
            {
                if (!sizing) return;
                var (vx, vy, vw, vh) = ComputeVideoRect();
                var pos = e.GetPosition(PlayerOverlayHost);
                double maxW = Math.Max(24, vx + vw - Canvas.GetLeft(v.Container));
                double maxH = Math.Max(24, vy + vh - Canvas.GetTop(v.Container));
                v.Container.Width = Math.Clamp(startW + (pos.X - start.X), 24, maxW);
                v.Container.Height = Math.Clamp(startH + (pos.Y - start.Y), 24, maxH);
            };
            grip.MouseLeftButtonUp += (s, e) =>
            {
                if (!sizing) return;
                sizing = false;
                grip.ReleaseMouseCapture();
                e.Handled = true;
                CommitOverlayRect(v);
            };

            v.Container.Children.Add(grip);
        }

        // After a drag/resize: store the new normalised rect and tell the owning plugin so it can persist it.
        private void CommitOverlayRect(OverlayVisual v)
        {
            var (vx, vy, vw, vh) = ComputeVideoRect();
            if (vw <= 0 || vh <= 0) return;
            var rect = new PlayerOverlayRect(
                Math.Clamp((Canvas.GetLeft(v.Container) - vx) / vw, 0.0, 1.0),
                Math.Clamp((Canvas.GetTop(v.Container) - vy) / vh, 0.0, 1.0),
                Math.Clamp(v.Container.Width / vw, 0.0, 1.0),
                Math.Clamp(v.Container.Height / vh, 0.0, 1.0));
            v.Rect = rect;
            var cb = v.Overlay.OnRectChanged;
            if (cb == null) return;
            try { cb(rect); }
            catch (Exception ex) { Logger.Error("Player overlay OnRectChanged handler threw.", ex); }
        }

        private void DetachOverlayContent(PlayerOverlay ov)
        {
            if (ov.Content is FrameworkElement fe) DetachFromParent(fe);
        }

        private static void DetachFromParent(FrameworkElement element)
        {
            switch (element.Parent)
            {
                case Panel p: p.Children.Remove(element); break;
                case Decorator d: d.Child = null; break;   // Border is a Decorator
                case ContentControl cc: cc.Content = null; break;
            }
        }
    }
}
