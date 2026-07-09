using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Wisp.Models;

namespace Wisp.Services
{
    /// <summary>
    /// Decides, in "Auto" recording mode, when a game is in the foreground so the rolling buffer can be
    /// started and stopped automatically - no hand-maintained list of game executables required.
    ///
    /// Detection is layered, checked in priority order (see <see cref="IsGameForeground"/>): explicit user
    /// overrides, then the user's installed-game catalog (Steam/Epic/GOG), then a behavioral heuristic
    /// (exclusive/borderless fullscreen + GPU 3D-engine usage). It is all external - no injection or hooking
    /// into the game - so it carries no anti-cheat risk.
    ///
    /// The work happens on a single background timer. State changes are debounced (a couple of ticks to
    /// start, a settings-controlled grace period to stop) and surfaced as <see cref="RecordingShouldStart"/>
    /// / <see cref="RecordingShouldStop"/> edges so the host can drive the existing buffer start/stop.
    /// </summary>
    public class GameDetectionService
    {
        private const int PollMs = 2000;
        private const int StartTicks = 2;          // consecutive positive ticks before we start (debounce blips)
        private const double GpuMinFullscreen3DPercent = 10; // 3D-engine usage that counts as a game when fullscreen
        private const double GpuMinWindowed3DPercent = 40;   // higher bar when windowed, so ordinary GPU apps don't trip it
        private static readonly TimeSpan RescanInstalledGamesEvery = TimeSpan.FromMinutes(5);

        private readonly AppSettings _settings;
        private readonly GpuUsageProbe _gpu = new();

        private Timer? _timer;
        private readonly object _lifecycleLock = new();
        private readonly object _tickLock = new();

        private InstalledGames _installed = new();
        private DateTime _installedScannedUtc = DateTime.MinValue;

        // Debounce / edge state, only touched inside a tick.
        private int _positiveTicks;
        private DateTime _lastGamePresentUtc = DateTime.MinValue;
        private bool _desiredRecording;
        private string? _currentGameKey;     // identity (process name) of the game we're recording for
        private uint _activeGamePid;         // PID we latched onto; recording continues while this process lives
        private bool _prevIsGame;            // for transition logging

        // Manual override interplay (set from the host when the user uses the tray toggle in Auto mode):
        private string? _suppressedGameKey;  // don't auto-start again for this exact game (user force-stopped it)
        private volatile bool _manualHold;   // user force-started: don't auto-stop until they stop

        /// <summary>Raised (on a background thread) when a game appears and recording should begin. Arg = display name.</summary>
        public event Action<string>? RecordingShouldStart;

        /// <summary>Raised (on a background thread) when the game is gone past the grace period and recording should stop.</summary>
        public event Action? RecordingShouldStop;

        /// <summary>
        /// Raised (on a background thread) when the monitor the recording game is on changes - i.e. the
        /// game window was dragged to another display. Arg = the new monitor's device name. The host uses
        /// this to re-target capture in "Auto" record-monitor mode.
        /// </summary>
        public event Action<string>? GameMonitorChanged;

        /// <summary>The game currently driving auto-recording, for display. Null when nothing is detected.</summary>
        public string? CurrentGame { get; private set; }

        /// <summary>Device name of the monitor the recording game is currently on. Null when idle.</summary>
        public string? CurrentGameMonitorKey { get; private set; }

        public GameDetectionService(AppSettings settings)
        {
            _settings = settings;
        }

        public void Start()
        {
            lock (_lifecycleLock)
            {
                if (_timer != null) return;
                Logger.Info("Game auto-detection started.");
                ResetState();
                if (_settings.ImportInstalledGames) RefreshInstalledGames();
                _timer = new Timer(_ => Tick(), null, 0, PollMs);
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (_timer == null) return;
                Logger.Info("Game auto-detection stopped.");
                _timer.Dispose();
                _timer = null;
            }
        }

        public bool IsRunning => _timer != null;

        /// <summary>User force-stopped recording while in Auto mode: stay stopped for this same game.</summary>
        public void NotifyManuallyStopped()
        {
            _manualHold = false;
            _desiredRecording = false;
            _suppressedGameKey = _currentGameKey;
            CurrentGame = null;
            CurrentGameMonitorKey = null;
        }

        /// <summary>User force-started recording while in Auto mode: don't auto-stop it.</summary>
        public void NotifyManuallyStarted()
        {
            _manualHold = true;
            _desiredRecording = true;
            _suppressedGameKey = null;
        }

        private void ResetState()
        {
            _positiveTicks = 0;
            _lastGamePresentUtc = DateTime.MinValue;
            _desiredRecording = false;
            _currentGameKey = null;
            _activeGamePid = 0;
            _prevIsGame = false;
            _suppressedGameKey = null;
            _manualHold = false;
            CurrentGame = null;
            CurrentGameMonitorKey = null;
        }

        private void Tick()
        {
            // Skip if a previous (slow) tick is still running - never let ticks pile up.
            if (!Monitor.TryEnter(_tickLock)) return;
            try
            {
                if (_settings.ImportInstalledGames &&
                    DateTime.UtcNow - _installedScannedUtc > RescanInstalledGamesEvery)
                    RefreshInstalledGames();

                IntPtr hwnd = GetForegroundWindow();
                uint pid = 0;
                if (hwnd != IntPtr.Zero) GetWindowThreadProcessId(hwnd, out pid);
                string name = GetProcessName(pid);

                bool fgIsGame = IsGameForeground(hwnd, pid, name, out string reason);

                // Auto-follow: while recording a game, note which monitor it's on whenever the game itself
                // is focused (hwnd is then the game window), and signal on change. Sampling only while the
                // game is foreground means alt-tabbing to an app on another display never drags capture off
                // the game.
                if (_desiredRecording && fgIsGame && hwnd != IntPtr.Zero)
                {
                    try
                    {
                        string monKey = System.Windows.Forms.Screen.FromHandle(hwnd).DeviceName;
                        if (!string.Equals(monKey, CurrentGameMonitorKey, StringComparison.OrdinalIgnoreCase))
                        {
                            CurrentGameMonitorKey = monKey;
                            GameMonitorChanged?.Invoke(monKey);
                        }
                    }
                    catch { }
                }

                // ---- While recording: keep going until the game's PROCESS exits, not while it has focus.
                // Alt-tabbing to the desktop or another app no longer stops the buffer - only the game
                // actually closing does. (Manual-hold recordings are left entirely to the user.)
                if (_desiredRecording && !_manualHold)
                {
                    bool latchedAlive = IsProcessAlive(_activeGamePid, _currentGameKey);
                    if (latchedAlive || fgIsGame)
                    {
                        _lastGamePresentUtc = DateTime.UtcNow;
                        // Latched game closed but a different game is in front now - re-latch onto it so a
                        // back-to-back game switch keeps recording without a stop/start.
                        if (!latchedAlive && fgIsGame)
                        {
                            _activeGamePid = pid;
                            _currentGameKey = name;
                        }
                    }
                    else
                    {
                        double idle = (DateTime.UtcNow - _lastGamePresentUtc).TotalSeconds;
                        if (idle >= Math.Max(1, _settings.AutoRecordStopGraceSeconds))
                        {
                            _desiredRecording = false;
                            _activeGamePid = 0;
                            _currentGameKey = null;
                            CurrentGame = null;
                            CurrentGameMonitorKey = null;
                            _prevIsGame = false;
                            Logger.Info("Auto-detect: stopping recording (game process exited).");
                            RecordingShouldStop?.Invoke();
                        }
                    }
                    return;
                }

                // ---- Not recording: watch the foreground for a game to start on.
                if (fgIsGame != _prevIsGame)
                {
                    Logger.Info(fgIsGame
                        ? $"Auto-detect: game in foreground = '{name}' ({reason})."
                        : "Auto-detect: foreground is not a game.");
                    _prevIsGame = fgIsGame;
                }

                if (fgIsGame)
                {
                    _positiveTicks++;
                    string gameKey = name;

                    // A different game than the one we suppressed means the user moved on - re-enable auto-start.
                    if (_suppressedGameKey != null && !string.Equals(gameKey, _suppressedGameKey, StringComparison.OrdinalIgnoreCase))
                        _suppressedGameKey = null;

                    bool suppressed = string.Equals(gameKey, _suppressedGameKey, StringComparison.OrdinalIgnoreCase);
                    if (!_desiredRecording && !_manualHold && !suppressed && _positiveTicks >= StartTicks)
                    {
                        _desiredRecording = true;
                        _activeGamePid = pid;
                        _currentGameKey = gameKey;
                        _lastGamePresentUtc = DateTime.UtcNow;
                        try { CurrentGameMonitorKey = System.Windows.Forms.Screen.FromHandle(hwnd).DeviceName; } catch { CurrentGameMonitorKey = null; }
                        string display = FFmpegRecorderService.GetForegroundGameName();
                        if (string.IsNullOrWhiteSpace(display)) display = name;
                        CurrentGame = display;
                        Logger.Info($"Auto-detect: starting recording for '{display}' (pid {pid}).");
                        RecordingShouldStart?.Invoke(display);
                    }
                }
                else
                {
                    _positiveTicks = 0;
                    if (_suppressedGameKey != null) _suppressedGameKey = null; // foreground left the game
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Game auto-detection tick failed.", ex);
            }
            finally
            {
                Monitor.Exit(_tickLock);
            }
        }

        /// <summary>
        /// The detection decision tree, highest-confidence first. <paramref name="reason"/> is filled for logging.
        /// </summary>
        private bool IsGameForeground(IntPtr hwnd, uint pid, string name, out string reason)
        {
            reason = "";
            if (hwnd == IntPtr.Zero || pid == 0 || string.IsNullOrEmpty(name)) return false;

            // Never the desktop shell or Wisp itself.
            if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Wisp", StringComparison.OrdinalIgnoreCase))
                return false;

            // 1. Explicit user overrides win outright.
            if (MatchesAny(name, _settings.AlwaysRecordProcesses)) { reason = "always-record list"; return true; }
            if (MatchesAny(name, _settings.NeverRecordProcesses)) { reason = "never-record list"; return false; }

            // 2. Known installed game (Steam/Epic/GOG). Try the full path first, fall back to the exe name.
            if (_settings.ImportInstalledGames && _installed.Any)
            {
                string? exePath = GetProcessPath(pid);
                if (_installed.IsGame(exePath, name)) { reason = "installed game"; return true; }
            }

            // 3. Behavioral heuristic. Exclusive D3D fullscreen is an unambiguous game signal.
            if (IsD3DFullscreen()) { reason = "D3D fullscreen"; return true; }

            bool fullscreen = CoversWholeMonitor(hwnd);

            // GPU 3D-engine usage is the only signal that confirms a windowed game (e.g. Roblox) or that a
            // plain fullscreen window is really a game, so sample the counters lazily - right here, at the
            // point of use. Every earlier step (desktop/explorer/Wisp, deny-list, allow-list, installed-game
            // catalog, exclusive D3D fullscreen) already decided WITHOUT the GPU, so on the common idle ticks
            // we now skip PerformanceCounterCategory.ReadCategory() entirely. That call snapshots every GPU
            // engine instance on the machine and is this loop's single biggest allocator; gating it keeps the
            // idle managed heap almost flat.
            //
            // Trade-off: utilization is a delta between two samples, so the first tick after an unknown window
            // appears has no baseline and reads 0% for it; the next tick (PollMs later) has the baseline and
            // reads the true value. With StartTicks already requiring two positive ticks, the worst case is a
            // *windowed, non-cataloged* game starting one tick later than before. Fullscreen/D3D and
            // catalog/allow-list games never reach here, so they're unaffected.
            if (_gpu.Available) _gpu.Sample();

            // Fullscreen but no GPU counters to confirm with: trust the fullscreen signal alone (browsers and
            // video players were already excluded by the denylist above).
            if (fullscreen && !_gpu.Available) { reason = "fullscreen (no GPU counters)"; return true; }

            if (_gpu.Available)
            {
                // Real 3D-engine work for the foreground process. The 3D-vs-VideoDecode split keeps
                // fullscreen video out. Fullscreen needs only modest usage; a windowed app must be driving
                // the GPU hard - that catches windowed games like Roblox without tripping on the odd
                // GPU-accelerated desktop app.
                double gpu3d = _gpu.Get3DUtilizationForPid(pid);
                if (fullscreen && gpu3d >= GpuMinFullscreen3DPercent) { reason = $"fullscreen + GPU 3D {gpu3d:0}%"; return true; }
                if (!fullscreen && gpu3d >= GpuMinWindowed3DPercent) { reason = $"windowed + GPU 3D {gpu3d:0}%"; return true; }
            }

            return false;
        }

        private void RefreshInstalledGames()
        {
            try
            {
                _installed = InstalledGameScanner.Scan();
                _installedScannedUtc = DateTime.UtcNow;
            }
            catch (Exception ex) { Logger.Warn($"Installed-game refresh failed: {ex.Message}"); }
        }

        private static bool MatchesAny(string name, System.Collections.Generic.List<string> list)
        {
            foreach (string entry in list)
                if (!string.IsNullOrEmpty(entry) && name.StartsWith(entry, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static string GetProcessName(uint pid)
        {
            if (pid == 0) return "";
            try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); return p.ProcessName ?? ""; }
            catch { return ""; }
        }

        /// <summary>True while the PID is a live process whose name still matches - the name check guards
        /// against Windows reusing the PID for an unrelated process after the game closes.</summary>
        private static bool IsProcessAlive(uint pid, string? expectedName)
        {
            if (pid == 0) return false;
            try
            {
                // GetProcessById throws if the PID isn't running; ProcessName comes from the system
                // snapshot, so both work without a handle - important for elevated/anti-cheat games (e.g.
                // Valorant) where opening a handle (HasExited) would be denied and look like an exit.
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                return string.IsNullOrEmpty(expectedName) ||
                       string.Equals(p.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ===================== Win32 =====================

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        /// <summary>True when any app currently owns the screen in exclusive Direct3D fullscreen.</summary>
        private static bool IsD3DFullscreen()
        {
            try { return SHQueryUserNotificationState(out int s) == 0 && s == QUNS_RUNNING_D3D_FULL_SCREEN; }
            catch { return false; }
        }

        /// <summary>True when the window fills (or exceeds) its monitor - borderless or exclusive fullscreen.</summary>
        private static bool CoversWholeMonitor(IntPtr hwnd)
        {
            try
            {
                if (!GetWindowRect(hwnd, out RECT r)) return false;
                IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero) return false;
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(mon, ref mi)) return false;
                return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                       r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
            }
            catch { return false; }
        }

        /// <summary>Full exe path for a PID, or null if it can't be read (e.g. an elevated process).</summary>
        private static string? GetProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                int cap = 1024;
                var sb = new StringBuilder(cap);
                return QueryFullProcessImageName(h, 0, sb, ref cap) ? sb.ToString() : null;
            }
            catch { return null; }
            finally { CloseHandle(h); }
        }
    }
}
