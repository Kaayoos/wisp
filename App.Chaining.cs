using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Wisp.Models;
using Wisp.Services;

namespace Wisp
{
    /// <summary>
    /// Clip chaining: when the hotkey is tapped several times within a short window, the overlapping
    /// rolling buffers are stitched into ONE continuous longer clip (with a marker per tap) instead of
    /// separate files - for "that was insane… wait, it's STILL happening" moments.
    ///
    /// Shape of the feature:
    ///   • The FIRST tap saves a normal clip immediately (snappy + crash-safe) and opens a chain session.
    ///   • Each tap within ChainWindowSeconds of the previous one drops a marker and re-arms the window.
    ///   • When the window lapses (or the chain hits the length ceiling) the chain finalizes: if there were
    ///     ≥2 taps, the recorder re-assembles ONE clip spanning [firstTap - buffer, newest] and the short
    ///     first-tap clip is replaced by it; a lone tap just keeps the clip already saved.
    ///
    /// All chain state below is only ever touched on the UI/dispatcher thread (the hotkey hook marshals
    /// onto it, and the window timer is a DispatcherTimer), so it needs no extra locking.
    /// </summary>
    public partial class App : Application
    {
        // A short tail kept after the final tap so the stitched clip captures a beat of aftermath.
        private const double ChainTailSeconds = 2.0;

        private bool _chainActive;
        private bool _chainFinalizing;                 // true while a finalize is committing; taps are ignored
        private DateTime _chainFirstTapUtc;
        private DateTime _chainWindowStartUtc;         // firstTap - buffer; the clip's intended start
        private readonly List<DateTime> _chainTapsUtc = new();
        private string _chainGameName = "";
        private Task<Clip?>? _chainFirstSaveTask;       // the in-flight/finished first-tap save
        private DispatcherTimer? _chainTimer;           // fires ChainWindowSeconds after the last tap
        private NotificationOverlay? _chainOverlay;     // the live growing-chain toast

        /// <summary>Handles one hotkey tap while chaining is enabled. Runs on the UI thread.</summary>
        private void HandleChainTap()
        {
            // A finalize is mid-flight (re-assembling + swapping files). Ignore taps until it settles so we
            // don't race the audio-retention reset or the clip replacement; the user can tap again after.
            if (_chainFinalizing)
            {
                Logger.Info("Chain tap ignored: a chain is currently finalizing.");
                return;
            }

            var now = DateTime.UtcNow;

            if (!_chainActive)
            {
                StartChain(now);
            }
            else
            {
                ExtendChain(now);
            }
        }

        private void StartChain(DateTime now)
        {
            _chainActive = true;
            _chainFinalizing = false;
            _chainFirstTapUtc = now;
            _chainWindowStartUtc = now.AddSeconds(-_settings.BufferLengthSeconds);
            _chainTapsUtc.Clear();
            _chainTapsUtc.Add(now);

            // Snapshot the focused game/app now, before the overlay can take foreground.
            _chainGameName = FFmpegRecorderService.GetForegroundGameName();

            // Keep enough audio history alive that the first tap's pre-roll survives until finalize, no
            // matter how long the chain runs (bounded by the chain ceiling). The extra ChainWindow covers
            // the gap between the last tap and finalize, when the audio is actually snapshotted.
            _audioManager.SetChainRetentionSeconds(_settings.EffectiveMaxChainedClipSeconds + _settings.ChainWindowSeconds);

            // First tap shows the normal capture popup, exactly like a non-chained capture (it might never
            // chain). Honor the "show capture progress" preference like the classic path.
            try
            {
                if (_settings.ShowCaptureProgress)
                {
                    _chainOverlay = new NotificationOverlay();
                    _chainOverlay.Show();
                }
                else
                {
                    ShowResultOnlyNotification(true, ""); // instant "Clip captured" + chime
                    _chainOverlay = null;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to show capture overlay.", ex);
                _chainOverlay = null;
            }

            // Save the first-tap clip right away (the normal path) so a single tap behaves like today.
            _chainFirstSaveTask = SaveFirstChainClipAsync(_chainGameName);

            ArmChainTimer();
            Logger.Info($"Clip chain started (window {_settings.ChainWindowSeconds}s, ceiling {_settings.EffectiveMaxChainedClipSeconds}s).");
        }

        private void ExtendChain(DateTime now)
        {
            _chainTapsUtc.Add(now);
            int count = _chainTapsUtc.Count;
            SoundManager.PlayChainTickSound(_settings);

            // Replace whatever capture/chain popup is on screen with a fresh, brief "Chained ×N" one -
            // no countdown, no lingering. It auto-dismisses on its own like the other result popups.
            try
            {
                try { _chainOverlay?.Close(); } catch { }
                _chainOverlay = new NotificationOverlay();
                _chainOverlay.SetChainCount(count);
                _chainOverlay.Show();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to show chain count overlay.", ex);
                _chainOverlay = null;
            }

            Logger.Info($"Clip chain extended to {count} taps.");

            // If the stitched clip would reach the length ceiling, finalize now rather than drop frames.
            double projected = (now - _chainWindowStartUtc).TotalSeconds;
            if (projected >= _settings.EffectiveMaxChainedClipSeconds)
            {
                Logger.Info($"Clip chain hit the {_settings.EffectiveMaxChainedClipSeconds}s ceiling; finalizing now.");
                FinalizeChain();
                return;
            }

            ArmChainTimer();
        }

        private void ArmChainTimer()
        {
            if (_chainTimer == null)
            {
                _chainTimer = new DispatcherTimer();
                _chainTimer.Tick += (s, e) =>
                {
                    _chainTimer!.Stop();
                    FinalizeChain();
                };
            }
            _chainTimer.Stop();
            _chainTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, _settings.ChainWindowSeconds));
            _chainTimer.Start();
        }

        /// <summary>
        /// Saves the first-tap clip on a background thread (same pipeline as a normal capture). Updates the
        /// library + overlay when it lands. Returns the saved clip so the finalize step knows which short
        /// clip to replace if the chain ends up stitching.
        /// </summary>
        private Task<Clip?> SaveFirstChainClipAsync(string gameName)
        {
            return Task.Run(async () =>
            {
                try
                {
                    var clip = await _recorderService.SaveClipAsync(_settings, _audioManager, gameName);
                    Dispatcher.Invoke(() =>
                    {
                        // Only resolve the capture popup if no follow-up tap has turned this into a chain
                        // yet - once chaining, the per-tap "Chained ×N" popups own the notification.
                        bool stillSingle = _chainActive && _chainTapsUtc.Count == 1;
                        bool showProgress = _settings.ShowCaptureProgress;
                        if (clip != null)
                        {
                            _mainWindow?.LoadClips();
                            if (stillSingle)
                            {
                                if (showProgress) _chainOverlay?.UpdateToSuccess(); // instant mode already said so
                                ShowToastNotification(clip);
                            }
                        }
                        else if (stillSingle)
                        {
                            if (showProgress) _chainOverlay?.UpdateToFailure("Failed to assemble clip.");
                            else ShowResultOnlyNotification(false, "Clip could not be saved.");
                        }
                    });
                    return clip;
                }
                catch (Exception ex)
                {
                    Logger.Error("First chain clip save failed.", ex);
                    Dispatcher.Invoke(() =>
                    {
                        if (_chainActive && _chainTapsUtc.Count == 1)
                        {
                            if (_settings.ShowCaptureProgress) _chainOverlay?.UpdateToFailure("Save error occurred.");
                            else ShowResultOnlyNotification(false, "Clip could not be saved.");
                        }
                    });
                    return null;
                }
            });
        }

        /// <summary>
        /// Commits the chain. A lone tap keeps the clip already saved; ≥2 taps re-assemble one stitched clip
        /// spanning the whole chain and replace the short first-tap clip with it. Runs on the UI thread.
        /// </summary>
        private async void FinalizeChain()
        {
            if (!_chainActive || _chainFinalizing) return;
            _chainFinalizing = true;
            _chainTimer?.Stop();

            // Snapshot the session.
            var taps = new List<DateTime>(_chainTapsUtc);
            var windowStart = _chainWindowStartUtc;
            var gameName = _chainGameName;
            var firstSaveTask = _chainFirstSaveTask;

            // Make sure the first-tap save has finished so we know which clip to replace.
            Clip? baseClip = null;
            try { if (firstSaveTask != null) baseClip = await firstSaveTask; }
            catch (Exception ex) { Logger.Error("Awaiting first chain save failed.", ex); }

            if (taps.Count < 2)
            {
                // Just a single tap - keep the clip that already saved (its capture popup already showed).
                _audioManager.SetChainRetentionSeconds(0);
                ResetChainState();
                return;
            }

            Logger.Info($"Finalizing clip chain of {taps.Count} taps into one stitched clip.");

            // End the stitched clip a short beat after the LAST tap (a touch of aftermath), rather than
            // letting it trail the entire chain window of dead footage.
            DateTime windowEnd = taps[taps.Count - 1].AddSeconds(ChainTailSeconds);
            DateTime createdAt = baseClip?.CreatedAt ?? DateTime.Now;
            Clip? chained = null;
            try
            {
                chained = await _recorderService.AssembleChainedClipAsync(
                    _settings, _audioManager, gameName, windowStart, windowEnd, taps, createdAt);
            }
            catch (Exception ex)
            {
                Logger.Error("Chain finalize assembly failed.", ex);
            }

            // The assembly has already snapshotted the audio it needs, so retention can return to normal.
            _audioManager.SetChainRetentionSeconds(0);

            if (chained != null)
            {
                // The stitched clip supersedes the short first-tap clip - remove that one's row + files.
                if (baseClip != null)
                {
                    try
                    {
                        _dbService.DeleteClipAndFiles(baseClip);
                        // The first-tap clip was replaced by the stitched one - let plugins (e.g. an
                        // uploader) dedupe a clip they may have already seen via ClipSaved.
                        _pluginManager?.RaiseClipRemoved(baseClip, superseded: true);
                    }
                    catch (Exception ex) { Logger.Error("Failed to remove the superseded first-tap clip.", ex); }
                }

                _mainWindow?.LoadClips();
                ShowToastNotification(chained);
            }
            else
            {
                // Stitching failed - keep whatever the first tap saved (still in the library).
                _mainWindow?.LoadClips();
            }

            ResetChainState();
        }

        private void ResetChainState()
        {
            _chainActive = false;
            _chainFinalizing = false;
            _chainTapsUtc.Clear();
            _chainOverlay = null;
            _chainFirstSaveTask = null;
            _chainGameName = "";
        }
    }
}
