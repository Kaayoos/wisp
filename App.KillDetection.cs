using System;
using System.Windows;
using System.Windows.Threading;
using Wisp.Services;
using Wisp.Services.KillDetection;

namespace Wisp
{
    /// <summary>
    /// Kill detection, app side: owns the <see cref="KillDetectionService"/> lifecycle and turns its
    /// kill events into optional auto-clips.
    ///
    /// Shape of the feature:
    ///   • The service (Services/KillDetection) watches the running game through anti-cheat-safe
    ///     channels and keeps a rolling history of kill instants. Clip assembly reads that history via
    ///     FFmpegRecorderService.KillTimestampProvider, so EVERY saved clip - manual or automatic -
    ///     gets kill markers stamped where kills landed. No app-side state is needed for markers.
    ///   • Auto-clip (opt-in) fires the EXACT hotkey path a few seconds after each kill, so the
    ///     aftermath is in the buffer and clip chaining naturally stitches multikills into one clip.
    ///
    /// All state below is only ever touched on the UI/dispatcher thread (kill events are marshalled
    /// onto it), matching the chaining partial's threading model.
    /// </summary>
    public partial class App : Application
    {
        private KillDetectionService? _killDetection;

        // Auto-clip throttle state (UI thread only). _lastAutoClipUtc is set when a clip is SCHEDULED,
        // not when it fires, so a burst of kills inside the cooldown collapses into the first clip
        // instead of queueing several overlapping saves (with chaining on, the chain window handles
        // bursts instead and the cooldown is not used).
        private DateTime _lastAutoClipUtc = DateTime.MinValue;

        /// <summary>
        /// Starts or stops kill detection to match the current settings. Safe to call at any time
        /// (startup, after every settings save); with the master toggle off the service is fully
        /// stopped and nothing kill-related runs.
        /// </summary>
        public void ApplyKillDetectionSettings()
        {
            try
            {
                if (_settings.KillDetectionEnabled)
                    _killDetection?.Start();
                else
                    _killDetection?.Stop();
            }
            catch (Exception ex)
            {
                Logger.Error("Applying kill detection settings failed.", ex);
            }
        }

        /// <summary>One detected kill, marshalled onto the UI thread. Schedules an auto-clip if enabled.</summary>
        private void OnKillDetected(DateTime killUtc)
        {
            try
            {
                // Markers need nothing here - the assembler pulls kill history itself. This handler
                // only drives the optional auto-clip.
                if (!_settings.KillAutoClipEnabled) return;
                if (_recorderService == null || !_recorderService.IsRecording) return;

                var now = DateTime.UtcNow;
                if (!_settings.ClipChainingEnabled)
                {
                    int cooldown = Math.Clamp(_settings.KillAutoClipCooldownSeconds, 5, 60);
                    if ((now - _lastAutoClipUtc).TotalSeconds < cooldown)
                    {
                        Logger.Info("Kill auto-clip skipped (cooldown).");
                        return;
                    }
                }
                _lastAutoClipUtc = now;

                // Give the buffer a beat to record the aftermath, then fire the normal hotkey path:
                // chaining, capture overlay, chime, and toast all behave exactly as for a manual tap.
                // (If a chain finalize is mid-flight when this lands, the tap is dropped and logged,
                // same as a human tap - acceptable.)
                int delay = Math.Clamp(_settings.KillAutoClipDelaySeconds, 0, 10);
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    if (_recorderService != null && _recorderService.IsRecording)
                    {
                        Logger.Info("Kill auto-clip: capturing.");
                        OnHotkeyTriggered();
                    }
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                Logger.Error("Kill auto-clip handling failed.", ex);
            }
        }
    }
}
