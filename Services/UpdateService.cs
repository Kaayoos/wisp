using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Wisp.Services
{
    /// <summary>
    /// Thin wrapper around Velopack that keeps Wisp on the newest GitHub release. It checks
    /// https://github.com/Kaayoos/wisp for a newer build and downloads it in the background, but defers
    /// actually installing it until the app exits - so an update never interrupts an active recording.
    ///
    /// Velopack only works inside a properly installed build (one laid out by its Setup.exe). When Wisp
    /// runs un-packaged - a dev/debug run, or a plain copied exe - <see cref="IsInstalled"/> is false and
    /// every method here is a safe no-op, so nothing changes for local development.
    /// </summary>
    public class UpdateService
    {
        // The public repo that hosts Wisp's releases (the Velopack Setup.exe + the update feed live here).
        private const string RepoUrl = "https://github.com/Kaayoos/wisp";

        private readonly UpdateManager _manager;
        private UpdateInfo? _pending;

        public UpdateService()
        {
            // accessToken null = public repo; prerelease false = only stable releases are offered.
            _manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
        }

        /// <summary>True only for a real installed build; false for dev/unpackaged runs (checks skipped).</summary>
        public bool IsInstalled
        {
            get { try { return _manager.IsInstalled; } catch { return false; } }
        }

        /// <summary>The version waiting to install (downloaded, applies on exit), or null if none.</summary>
        public string? PendingVersion => _pending?.TargetFullRelease?.Version?.ToString();

        /// <summary>
        /// Checks GitHub for a newer release and, if there is one, downloads it in the background and
        /// stages it. Returns the new version string when something was staged (so the caller can notify
        /// the user), or null when already up to date / not installed / on any error. Never throws - this
        /// is best-effort and must never take the app down.
        /// </summary>
        public async Task<string?> CheckAndDownloadAsync()
        {
            try
            {
                if (!_manager.IsInstalled) return null; // dev / unpackaged run - nothing to update

                UpdateInfo? info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
                if (info == null) return null; // already on the newest release

                await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
                _pending = info;

                string version = info.TargetFullRelease.Version.ToString();
                Logger.Info($"Update {version} downloaded and staged; it will install when Wisp exits.");
                return version;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Update check/download failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// If an update has been staged, hands it to Velopack to install AFTER this process exits, with no
        /// auto-restart - so the user simply gets the new version the next time they open Wisp. Call this
        /// right before the app terminates. No-op when nothing is staged.
        /// </summary>
        public void ApplyPendingOnExit()
        {
            try
            {
                if (_pending == null) return;
                Logger.Info("Scheduling staged update to install on exit.");
                _manager.WaitExitThenApplyUpdates(_pending.TargetFullRelease, silent: true, restart: false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not schedule update apply-on-exit: {ex.Message}");
            }
        }
    }
}
