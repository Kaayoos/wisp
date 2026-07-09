using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wisp.Plugins;
using Wisp.Plugins.Export;
using Wisp.Plugins.Player;
using Wisp.Plugins.Settings;

namespace WispWebcamOverlay
{
    /// <summary>
    /// Camera Overlay - records the webcam in the background while Wisp's buffer runs, but shows it ONLY
    /// inside saved clips: a movable, play-synced picture-in-picture in the player, and (optionally) burned
    /// into exports. Nothing is shown live, so your face isn't on screen the whole time you play.
    ///
    /// It's also a worked example of the v4 plugin API's two generic primitives, neither of which knows
    /// anything about "cameras":
    ///   • <see cref="IWispPlayer.AddOverlay"/> - host your own WPF content over the player video, movable,
    ///     synced to playback (here: a MediaElement of the captured camera file).
    ///   • <see cref="WispPluginBase.GetExportLayers"/> - hand the host an image/video to composite into the
    ///     exported clip (here: that same camera file, at the spot the user dragged it to).
    ///
    /// Everything camera-specific (capture, the rolling buffer, trimming to the clip, file management) lives
    /// here in the plugin - see <see cref="CameraRecorder"/>.
    /// </summary>
    public sealed class CameraOverlayPlugin : WispPluginBase
    {
        public override string Id => "wisp.webcam-overlay";
        public override string Name => "Camera Overlay";
        public override string Version => "2.0.0";
        public override string Author => "MinimalPulse";
        public override string Description => "Records your webcam in the background and shows it as a movable, synced overlay inside clips - optionally burned into exports.";

        private OverlaySettings _settings = new();
        private string _bufferDir = "";
        private string _clipsDir = "";

        private CameraRecorder? _recorder;

        // Wall-clock (UTC ticks) of the last capture hotkey - the clip's true end, used to align the camera.
        private long _lastHotkeyUtcTicks;

        // Player overlay state (UI thread only).
        private MediaElement? _camElement;
        private DispatcherTimer? _syncTimer;
        private bool _camPlaying;

        public override void OnEnabled()
        {
            _settings = Host.Storage.LoadSettings<OverlaySettings>() ?? new OverlaySettings();
            _bufferDir = Path.Combine(Host.Storage.DataDirectory, "buffer");
            _clipsDir = Path.Combine(Host.Storage.DataDirectory, "clips");

            Host.Events.RecordingStarted += OnRecordingStarted;
            Host.Events.RecordingStopped += OnRecordingStopped;
            Host.Events.ClipSaved += OnClipSaved;
            Host.Events.ClipRemoved += OnClipRemoved;
            Host.Events.HotkeyTriggered += OnHotkey;
            Host.Player.ClipOpened += OnPlayerClipOpened;
            Host.Player.Closed += OnPlayerClosed;

            if (Host.Recorder.IsRecording) StartRecorder();
            Host.Log.Info("Camera Overlay enabled.");
        }

        public override void OnDisabled()
        {
            Host.Events.RecordingStarted -= OnRecordingStarted;
            Host.Events.RecordingStopped -= OnRecordingStopped;
            Host.Events.ClipSaved -= OnClipSaved;
            Host.Events.ClipRemoved -= OnClipRemoved;
            Host.Events.HotkeyTriggered -= OnHotkey;
            Host.Player.ClipOpened -= OnPlayerClipOpened;
            Host.Player.Closed -= OnPlayerClosed;

            Host.Ui.RunOnUiThread(TeardownOverlay);
            StopRecorder();
            Host.Log.Info("Camera Overlay disabled.");
        }

        public override void OnShutdown()
        {
            Host.Ui.RunOnUiThread(TeardownOverlay);
            StopRecorder();
        }

        // ───────────────────────── recording (background) ─────────────────────────

        private void OnRecordingStarted(object? sender, RecordingEventArgs e) => StartRecorder();
        private void OnRecordingStopped(object? sender, RecordingEventArgs e) => StopRecorder();

        private void StartRecorder()
        {
            if (_recorder != null) return;
            var r = new CameraRecorder(_settings.CameraIndex, _bufferDir, _settings.BufferSeconds);
            r.Start();
            if (!r.IsAvailable)
            {
                Host.Log.Warn($"Camera index {_settings.CameraIndex} is unavailable; camera clips are off until it's connected.");
                r.Dispose();
                return;
            }
            _recorder = r;
            Host.Log.Info("Camera recording started (headless rolling buffer).");
        }

        private void StopRecorder()
        {
            var r = _recorder;
            _recorder = null;
            if (r == null) return;
            try { r.Dispose(); } catch { }
            Host.Log.Info("Camera recording stopped.");
        }

        // When a clip is saved, snapshot the matching tail of the camera buffer into cam_<id>.mp4. The event
        // is already on a background thread; the assembly (a short re-encode) runs off it so it can't stall
        // the other plugins' handlers.
        private void OnClipSaved(object? sender, ClipSavedEventArgs e)
        {
            var rec = _recorder;
            if (rec == null || !rec.IsAvailable) return;

            int id = e.Clip.Id;
            double dur = e.Clip.DurationSeconds;
            int offset = _settings.SyncOffsetMs;
            DateTime endUtc = HotkeyEndUtc(dur);
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    bool ok = rec.AssembleClip(CamPath(id), dur, endUtc, offset);
                    Host.Log.Info(ok ? $"Camera track saved for clip {id}." : $"No camera footage available for clip {id}.");
                }
                catch (Exception ex) { Host.Log.Error($"Failed to assemble camera for clip {id}: {ex.Message}"); }
            });
        }

        private void OnHotkey(object? sender, HotkeyEventArgs e)
            => System.Threading.Interlocked.Exchange(ref _lastHotkeyUtcTicks, DateTime.UtcNow.Ticks);

        // The clip's end on the capture clock: the last capture-hotkey press (the screen clip ends there too),
        // so the camera window lines up. Falls back to "now" if there's no recent hotkey (clip from elsewhere).
        private DateTime HotkeyEndUtc(double durationSeconds)
        {
            long ticks = System.Threading.Interlocked.Read(ref _lastHotkeyUtcTicks);
            if (ticks > 0)
            {
                var t = new DateTime(ticks, DateTimeKind.Utc);
                double age = (DateTime.UtcNow - t).TotalSeconds;
                if (age >= 0 && age < durationSeconds + 10) return t;
            }
            return DateTime.UtcNow;
        }

        private void OnClipRemoved(object? sender, ClipRemovedEventArgs e)
        {
            try { var p = CamPath(e.Clip.Id); if (File.Exists(p)) File.Delete(p); } catch { }
        }

        // ───────────────────────── player overlay (UI thread) ─────────────────────────

        private void OnPlayerClipOpened(object? sender, PlayerClipEventArgs e)
        {
            TeardownOverlay(); // clear any previous clip's overlay first

            string cam = CamPath(e.Clip.Id);
            if (!File.Exists(cam)) return;

            var media = new MediaElement
            {
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Manual,
                ScrubbingEnabled = true,   // render frames while paused / when we set Position
                Volume = 0,                // the clip already carries the audio
                Stretch = Stretch.UniformToFill,
                IsHitTestVisible = false   // let the host drag the overlay container beneath the cursor
            };
            if (_settings.Mirror)
            {
                media.RenderTransformOrigin = new Point(0.5, 0.5);
                media.RenderTransform = new ScaleTransform(-1, 1);
            }
            media.MediaFailed += (s, args) => Host.Log.Warn($"Camera overlay playback failed: {args.ErrorException?.Message}");
            media.Source = new Uri(cam);

            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xF2, 0xFF)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Black,
                ClipToBounds = true,
                Child = media
            };

            _camElement = media;
            _camPlaying = false;

            Host.Player.AddOverlay(new PlayerOverlay("camera", border)
            {
                X = _settings.PosX,
                Y = _settings.PosY,
                Width = _settings.SizeW,
                Height = _settings.SizeH,
                Movable = true,
                Resizable = true,
                Opacity = Math.Clamp(_settings.Opacity, 0.1, 1.0),
                OnRectChanged = OnOverlayMoved
            });

            media.Play();
            _camPlaying = true;
            StartSyncTimer();
        }

        private void OnPlayerClosed(object? sender, EventArgs e) => TeardownOverlay();

        // Persist the camera's spot whenever the user drags/resizes it, so it (and the export burn-in) stick.
        private void OnOverlayMoved(PlayerOverlayRect r)
        {
            _settings.PosX = r.X;
            _settings.PosY = r.Y;
            _settings.SizeW = r.Width;
            _settings.SizeH = r.Height;
            try { Host.Storage.SaveSettings(_settings); } catch { }
        }

        private void StartSyncTimer()
        {
            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _syncTimer.Tick += SyncTick;
            _syncTimer.Start();
        }

        // Keep the camera MediaElement locked to the main player's transport: mirror play/pause and correct
        // drift (also catches the clip looping back to 0).
        private void SyncTick(object? sender, EventArgs e)
        {
            var media = _camElement;
            if (media == null) return;
            try
            {
                bool hostPlaying = Host.Player.IsPlaying;
                double hostPos = Host.Player.PositionSeconds;

                if (hostPlaying && !_camPlaying) { media.Play(); _camPlaying = true; }
                else if (!hostPlaying && _camPlaying) { media.Pause(); _camPlaying = false; }

                double camPos = media.Position.TotalSeconds;
                if (Math.Abs(camPos - hostPos) > 0.25)
                    media.Position = TimeSpan.FromSeconds(Math.Max(0, hostPos));
            }
            catch { /* transient MediaElement state; the next tick retries */ }
        }

        private void TeardownOverlay()
        {
            if (_syncTimer != null)
            {
                _syncTimer.Stop();
                _syncTimer.Tick -= SyncTick;
                _syncTimer = null;
            }
            try { Host.Player.RemoveOverlay("camera"); } catch { }
            if (_camElement != null)
            {
                try { _camElement.Stop(); _camElement.Close(); _camElement.Source = null; } catch { }
                _camElement = null;
            }
            _camPlaying = false;
        }

        // ───────────────────────── export burn-in ─────────────────────────

        public override IReadOnlyList<ExportLayer>? GetExportLayers(ClipInfo clip)
        {
            string cam = CamPath(clip.Id);
            if (!File.Exists(cam)) return null;
            return new[]
            {
                new ExportLayer(cam)
                {
                    X = _settings.PosX,
                    Y = _settings.PosY,
                    Width = _settings.SizeW,
                    Height = _settings.SizeH,
                    Opacity = Math.Clamp(_settings.Opacity, 0.1, 1.0),
                    Mirror = _settings.Mirror
                }
            };
        }

        // ───────────────────────── settings ─────────────────────────

        public override IReadOnlyList<PluginSettingField>? GetSettings() => new PluginSettingField[]
        {
            new NumberSettingField("CameraIndex", "Camera Index", _settings.CameraIndex)
                { Min = 0, Max = 10, Step = 1, Description = "OpenCV camera index (0 = default webcam)" },
            new BoolSettingField("Mirror", "Mirror (selfie)", _settings.Mirror)
                { Description = "Horizontally flip the camera in the preview and export" },
            new NumberSettingField("Opacity", "Opacity", _settings.Opacity)
                { Min = 0.1, Max = 1.0, Step = 0.05, Description = "Overlay opacity in the player and export" },
            new NumberSettingField("BufferSeconds", "Replay seconds", _settings.BufferSeconds)
                { Min = 15, Max = 600, Step = 15, Description = "How much camera footage to keep ready. Set >= your replay length." },
            new NumberSettingField("SyncOffsetMs", "Sync offset (ms)", _settings.SyncOffsetMs)
                { Min = -2000, Max = 2000, Step = 50, Description = "Nudge timing if the face leads/lags the action" }
        };

        public override void OnSettingsSaved(IReadOnlyDictionary<string, object> newValues)
        {
            if (newValues.TryGetValue("CameraIndex", out var a)) _settings.CameraIndex = Convert.ToInt32(a);
            if (newValues.TryGetValue("Mirror", out var b)) _settings.Mirror = Convert.ToBoolean(b);
            if (newValues.TryGetValue("Opacity", out var c)) _settings.Opacity = Convert.ToDouble(c);
            if (newValues.TryGetValue("BufferSeconds", out var d)) _settings.BufferSeconds = Convert.ToInt32(d);
            if (newValues.TryGetValue("SyncOffsetMs", out var f)) _settings.SyncOffsetMs = Convert.ToInt32(f);
            try { Host.Storage.SaveSettings(_settings); } catch { }

            // Apply camera-index / buffer-length changes immediately by recycling the recorder.
            if (_recorder != null)
            {
                StopRecorder();
                if (Host.Recorder.IsRecording) StartRecorder();
            }
            Host.Log.Info("Camera Overlay settings updated.");
        }

        private string CamPath(int clipId) => Path.Combine(_clipsDir, $"cam_{clipId}.mp4");
    }
}
