using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Wisp.Models;

namespace Wisp.Services.KillDetection
{
    /// <summary>
    /// Hosts the per-game kill providers and keeps the rolling kill history that clip assembly stamps
    /// into <c>Clip.KillMarkers</c>.
    ///
    /// A ~2s router tick (same Timer + tick-guard shape as <see cref="GameDetectionService"/>) decides
    /// which providers should be live: master toggle on, recorder recording, that game's toggle on,
    /// the game's process running, and - for vision providers, which sample the screen - the game in
    /// the foreground. Providers are started/stopped on edges only. The service deliberately resolves
    /// game processes itself instead of listening to GameDetectionService events, because the detector
    /// is stopped entirely in "AlwaysOn"/"Manual" recording modes and kill detection must work in all
    /// three.
    ///
    /// With the master toggle off this service is never started: no timer, no providers, no sockets,
    /// no screen sampling - feature-off installs behave exactly as before the feature existed.
    /// </summary>
    public class KillDetectionService : IDisposable
    {
        private const int PollMs = 2000;

        private readonly AppSettings _settings;
        private readonly Func<bool> _isRecording;
        private readonly Func<string?> _gameMonitorKey;

        private Timer? _timer;
        private readonly object _lifecycleLock = new();
        private readonly object _tickLock = new();

        // Provider + its live per-game settings gate (reads the shared AppSettings instance, so
        // toggling a game in Settings takes effect on the next tick without restarting the service).
        private readonly List<(IKillProvider Provider, Func<bool> Enabled)> _providers = new();

        // Rolling history of detected kill instants, pruned to the deepest window a clip can cover.
        private readonly object _killsLock = new();
        private readonly List<DateTime> _killsUtc = new();

        /// <summary>Raised (on a background thread) for each detected kill, after it enters the history.</summary>
        public event Action<DateTime>? KillDetected;

        public bool IsRunning => _timer != null;

        public KillDetectionService(AppSettings settings, Func<bool> isRecording, Func<string?> gameMonitorKey)
        {
            _settings = settings;
            _isRecording = isRecording;
            _gameMonitorKey = gameMonitorKey;
        }

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_timer != null) return;
                Logger.Info("Kill detection started.");
                if (_providers.Count == 0) CreateProviders();
                _timer = new Timer(_ => Tick(), null, 0, PollMs);
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (_timer == null) return;
                Logger.Info("Kill detection stopped.");
                _timer.Dispose();
                _timer = null;
                foreach (var (provider, _) in _providers)
                {
                    try { provider.Stop(); } catch { /* stopping is best-effort */ }
                }
            }
        }

        public void Dispose()
        {
            Stop();
            lock (_lifecycleLock)
            {
                foreach (var (provider, _) in _providers)
                {
                    try { provider.Dispose(); } catch { }
                }
                _providers.Clear();
            }
        }

        /// <summary>
        /// The kill instants within [startUtc, endUtc], sorted ascending. Thread-safe - called from the
        /// clip-assembly worker to compute KillMarkers offsets.
        /// </summary>
        public IReadOnlyList<DateTime> GetKillsBetween(DateTime startUtc, DateTime endUtc)
        {
            var result = new List<DateTime>();
            lock (_killsLock)
            {
                foreach (var k in _killsUtc)
                    if (k >= startUtc && k <= endUtc)
                        result.Add(k);
            }
            result.Sort();
            return result;
        }

        private void CreateProviders()
        {
            // Construction is cheap and inert (nothing runs until Start() is called on a provider).
            // A provider that fails to construct (e.g. a bad embedded vision spec) skips that game
            // rather than taking the feature down.
            TryAdd(() => new LolKillProvider(), () => _settings.KillDetectLol);
            TryAdd(() => new Cs2KillProvider(_settings), () => _settings.KillDetectCs2);
            TryAdd(() => VisionKillProvider.FromEmbeddedSpec("valorant", _settings, _gameMonitorKey), () => _settings.KillDetectValorant);
            TryAdd(() => VisionKillProvider.FromEmbeddedSpec("overwatch2", _settings, _gameMonitorKey), () => _settings.KillDetectOverwatch);

            void TryAdd(Func<IKillProvider> make, Func<bool> enabled)
            {
                try
                {
                    var provider = make();
                    provider.KillDetected += utc => OnProviderKill(provider, utc);
                    _providers.Add((provider, enabled));
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Kill provider unavailable: {ex.Message}");
                }
            }
        }

        private void Tick()
        {
            // Skip if a previous (slow) tick is still running - never let ticks pile up.
            if (!Monitor.TryEnter(_tickLock)) return;
            try
            {
                bool globallyOn = _settings.KillDetectionEnabled && _isRecording();
                string foregroundName = globallyOn ? GetForegroundProcessName() : "";

                foreach (var (provider, enabled) in _providers)
                {
                    bool shouldRun = globallyOn && enabled()
                        && (!provider.RequiresForeground ||
                            string.Equals(foregroundName, provider.ProcessName, StringComparison.OrdinalIgnoreCase))
                        && ProcessExists(provider.ProcessName);

                    if (shouldRun && !provider.IsRunning)
                    {
                        Logger.Info($"Kill detection: starting {provider.GameName} provider.");
                        provider.Start();
                    }
                    else if (!shouldRun && provider.IsRunning)
                    {
                        Logger.Info($"Kill detection: stopping {provider.GameName} provider.");
                        provider.Stop();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Kill detection tick failed.", ex);
            }
            finally
            {
                Monitor.Exit(_tickLock);
            }
        }

        private void OnProviderKill(IKillProvider provider, DateTime utc)
        {
            try
            {
                lock (_killsLock)
                {
                    _killsUtc.Add(utc);

                    // Prune anything no clip window can reach anymore: the deepest window is the
                    // chained-clip ceiling (or the plain buffer), plus slack for assembly latency.
                    int horizonSeconds = Math.Max(_settings.EffectiveMaxChainedClipSeconds, _settings.BufferLengthSeconds) + 120;
                    DateTime cutoff = DateTime.UtcNow.AddSeconds(-horizonSeconds);
                    _killsUtc.RemoveAll(k => k < cutoff);
                }

                Logger.Info($"Kill detected ({provider.GameName}).");
                KillDetected?.Invoke(utc);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Kill event handling failed: {ex.Message}");
            }
        }

        private static bool ProcessExists(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            try
            {
                // Snapshot-based, no handle needed - works for elevated/anti-cheat games (e.g. Valorant)
                // exactly like GameDetectionService.IsProcessAlive.
                var procs = System.Diagnostics.Process.GetProcessesByName(processName);
                foreach (var p in procs) p.Dispose();
                return procs.Length > 0;
            }
            catch { return false; }
        }

        private static string GetForegroundProcessName()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "";
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return "";
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                return p.ProcessName ?? "";
            }
            catch { return ""; }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    }
}
