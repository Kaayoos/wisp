using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Wisp.Models;
using Wisp.Services;

namespace Wisp
{
    public partial class App : Application
    {
        private AppSettings _settings = null!;
        private DatabaseService _dbService = null!;
        private FFmpegRecorderService _recorderService = null!;
        private AudioCaptureManager _audioManager = null!;
        private KeyboardHookManager _hookManager = null!;
        private VoiceTriggerManager _voiceTrigger = null!;
        private GameDetectionService _gameDetector = null!;
        private TaskbarIcon _taskbarIcon = null!;
        private ContextMenu? _trayMenu;            // tray context menu (plugins insert items above Quit)
        private Separator? _trayPluginAnchor;      // plugin tray items are inserted just before this separator
        private Mutex? _singleInstanceMutex;
        private EventWaitHandle? _showWindowSignal;
        private const string SingleInstanceMutexName = @"Local\Wisp.SingleInstance";
        private const string ShowWindowEventName = @"Local\Wisp.ShowWindow";
        
        private MainWindow? _mainWindow;
        private MenuItem _toggleRecordMenuItem = null!;
        private bool _isSavingClip = false;
        private DispatcherTimer? _idleTrimTimer;
        private UpdateService? _updateService;   // Velopack auto-update (GitHub releases)

        public AppSettings Settings => _settings;
        public DatabaseService DbService => _dbService;
        public FFmpegRecorderService RecorderService => _recorderService;
        public AudioCaptureManager AudioManager => _audioManager;
        public KeyboardHookManager HookManager => _hookManager;
        public GameDetectionService GameDetector => _gameDetector;

        protected override void OnStartup(StartupEventArgs e)
        {
            // NOTE: Velopack's VelopackApp.Build().Run() runs earlier, in Program.Main, so update/install
            // hooks are handled before WPF (and this single-instance check) ever start.

            // Only one Wisp may run at a time (one recorder, one tray icon). A second launch just
            // surfaces the existing window and exits - this is what stops "many Wisps opening".
            if (!ClaimSingleInstance())
            {
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // Global unhandled exception handler to prevent silent crashes
            DispatcherUnhandledException += (s, args) =>
            {
                Logger.Error("Unhandled exception in UI dispatcher.", args.Exception);
                CustomMessageBox.Show($"An unexpected error occurred:\n\n{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                    "Wisp Error", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    Logger.Error("Fatal unhandled exception in AppDomain.", ex);
                }
            };

            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Logger.Error("Unobserved task exception.", args.Exception);
                args.SetObserved();
            };

            // 1. Load settings
            _settings = AppSettings.Load();

            // 1b. Apply the saved accent theme before any window is created, so the whole UI - including
            //     the first frame the user sees - is already in their chosen color.
            ThemeManager.Apply(_settings);

            // 2. Initialize SQLite DB
            _dbService = new DatabaseService();
            try
            {
                _dbService.CleanOrphanedClips();
                if (_settings.RetentionEnabled && _settings.RetentionDays > 0)
                {
                    _dbService.CleanupOldClips(_settings.RetentionDays);
                }
                if (_settings.MaxStorageEnabled)
                {
                    _dbService.EnforceStorageCap(GbToBytes(_settings.MaxStorageGB));
                }
            }
            catch (Exception ex)
            {
                Logger.Error("DB cleanup error during startup.", ex);
            }

            // 3. Initialize FFmpeg and Audio services & extract binary from embedded resource
            _recorderService = new FFmpegRecorderService(_dbService);
            _audioManager = new AudioCaptureManager();
            try
            {
                _recorderService.ExtractFFmpegFromResources();
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Failed to initialize recording backend (FFmpeg): {ex.Message}\n\nThe app will not be able to record clips.", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // 4. Initialize Tray Icon
            InitializeTrayIcon();

            // 5. Initialize Global Hotkey Hook
            _hookManager = new KeyboardHookManager();
            _hookManager.HotkeyTriggered += OnHotkeyTriggered;
            _hookManager.RegisterHotkey(_settings.GetHotkeyKeysList());

            // 5b. Voice trigger (offline, optional) - fires the same save path as the hotkey.
            _voiceTrigger = new VoiceTriggerManager();
            _voiceTrigger.PhraseDetected += OnHotkeyTriggered;

            // Reap any ffmpeg leaked by a previous crashed/force-quit session, before spawning ours.
            _recorderService.KillOrphanedFFmpeg();

            // 5c. First-run onboarding wizard
            if (!_settings.HasCompletedOnboarding)
            {
                var onboarding = new OnboardingWindow(_settings);
                if (onboarding.ShowDialog() != true)
                {
                    ShutdownApp();
                    return;
                }
                
                // Re-register hotkeys with new settings selected in wizard
                _hookManager.UnregisterHotkey();
                _hookManager.RegisterHotkey(_settings.GetHotkeyKeysList());
            }

            // 6. Game auto-detection. In "Auto" mode this owns when the buffer runs; the other modes are
            //    handled by ApplyRecordingModeSettings() below. Detector events arrive on a background
            //    thread, so marshal onto the UI thread before touching the recorder/tray.
            _gameDetector = new GameDetectionService(_settings);
            _gameDetector.RecordingShouldStart += game => Dispatcher.Invoke(() => OnAutoRecordStart(game));
            _gameDetector.RecordingShouldStop += () => Dispatcher.Invoke(OnAutoRecordStop);
            _gameDetector.GameMonitorChanged += key => Dispatcher.Invoke(() => OnGameMonitorChanged(key));
            ApplyRecordingModeSettings();

            // 6b. Kill detection (opt-in). The service self-resolves the running game each tick, so it
            //     works in every recording mode; with the master toggle off it is never started and
            //     nothing kill-related runs. Kill events arrive on a background thread - marshal onto
            //     the UI thread for the auto-clip path. Clip assembly pulls kill history through the
            //     recorder's KillTimestampProvider hook (thread-safe, worker-side).
            _killDetection = new Wisp.Services.KillDetection.KillDetectionService(
                _settings,
                () => _recorderService.IsRecording,
                () => _gameDetector?.CurrentGameMonitorKey);
            _killDetection.KillDetected += utc => Dispatcher.BeginInvoke(new Action(() => OnKillDetected(utc)));
            _recorderService.KillTimestampProvider = (startUtc, endUtc) =>
                (_settings.KillDetectionEnabled && _settings.KillMarkersEnabled && _killDetection != null)
                    ? _killDetection.GetKillsBetween(startUtc, endUtc)
                    : Array.Empty<DateTime>();
            ApplyKillDetectionSettings();

            // 7. Import clips already in the output folder (e.g. from before the rebrand) so they show
            //    up in the library. Background thread; refreshes the window if it happens to be open.
            var startupSettings = _settings;
            Task.Run(() =>
            {
                try
                {
                    int imported = _recorderService.ImportUntrackedClips(startupSettings.OutputFolder);
                    if (imported > 0)
                        Dispatcher.BeginInvoke(new Action(() => _mainWindow?.LoadClips()));
                }
                catch (Exception ex) { Logger.Error("Startup clip import failed.", ex); }
            });

            // 8. Load plugins last, once every core service + setting is ready. Plugins are disabled by
            //    default; only the ones the user enabled (in plugins.json) are activated here.
            InitializePlugins();

            // 9. Periodically hand background memory back to the OS. While Wisp sits in the tray the GC rarely
            //    runs a full collection (nothing allocates enough to trigger one), so the heap + working set
            //    drift upward even though almost all of it is reclaimable - opening then closing the window
            //    already proves that. This does the same reclaim on a timer so the footprint stays low on its
            //    own. Crucially it runs WHILE RECORDING too: the buffer is usually running the entire time
            //    Wisp is backgrounded (that's the point of an instant-replay recorder), and that's exactly
            //    when the old idle-only trim skipped and the RAM climbed. During recording we use the
            //    non-blocking TrimWhileBusy (no stop-the-world GC, so the audio thread is never suspended);
            //    when truly idle we use the fuller blocking TrimIdle. Never while the window is up - the user
            //    is interacting and trimming would just force everything to fault straight back in. The
            //    DispatcherTimer fires on the UI thread, so reading _mainWindow.IsVisible here is safe.
            _idleTrimTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMinutes(3)
            };
            _idleTrimTimer.Tick += (s, args) =>
            {
                if (_mainWindow != null && _mainWindow.IsVisible) return;
                if (_recorderService != null && _recorderService.IsRecording)
                    MemoryTrimmer.TrimWhileBusy();
                else
                    MemoryTrimmer.TrimIdle();
            };
            _idleTrimTimer.Start();

            // 10. Auto-update: check GitHub for a newer release in the background and stage it. It installs
            //     when the user quits Wisp (see ShutdownApp + UpdateService), so a recording is never cut off.
            StartUpdateCheck();
        }

        /// <summary>
        /// Acquires the cross-process single-instance lock. Returns false if another Wisp is already
        /// running - in which case it pings that instance to surface its window before we exit.
        /// </summary>
        private bool ClaimSingleInstance()
        {
            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var ev))
                    {
                        ev.Set();
                        ev.Dispose();
                    }
                }
                catch { }
                return false;
            }

            // First instance: listen for later launches asking us to show the window.
            _showWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            ThreadPool.RegisterWaitForSingleObject(_showWindowSignal,
                (state, timedOut) => Dispatcher.BeginInvoke(new Action(ShowMainWindow)),
                null, Timeout.Infinite, false);
            return true;
        }

        private void InitializeTrayIcon()
        {
            _taskbarIcon = new TaskbarIcon();
            // Wisp comet icon, loaded from the embedded branding artwork.
            _taskbarIcon.IconSource = CreateTrayIconSource();
            _taskbarIcon.ToolTipText = "Wisp - Game Clip Recorder";
            _taskbarIcon.TrayMouseDoubleClick += (s, e) => ShowMainWindow();

            var menu = new ContextMenu();
            _trayMenu = menu;

            var openItem = new MenuItem { Header = "Open Wisp" };
            openItem.Click += (s, e) => ShowMainWindow();
            menu.Items.Add(openItem);

            _toggleRecordMenuItem = new MenuItem { Header = "Stop Recording" };
            _toggleRecordMenuItem.Click += (s, e) => ToggleRecording();
            menu.Items.Add(_toggleRecordMenuItem);

            var settingsItem = new MenuItem { Header = "Settings" };
            settingsItem.Click += (s, e) => ShowSettingsWindow();
            menu.Items.Add(settingsItem);

            var updatesItem = new MenuItem { Header = "Check for updates" };
            updatesItem.Click += (s, e) => CheckForUpdatesManually();
            menu.Items.Add(updatesItem);

            _trayPluginAnchor = new Separator();
            menu.Items.Add(_trayPluginAnchor);

            var quitItem = new MenuItem { Header = "Quit" };
            quitItem.Click += (s, e) => ShutdownApp();
            menu.Items.Add(quitItem);

            _taskbarIcon.ContextMenu = menu;
        }

        /// <summary>
        /// Loads the Wisp tray icon from the embedded branding artwork.
        /// </summary>
        private static ImageSource CreateTrayIconSource()
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri("pack://application:,,,/img/WispIcon.png", UriKind.Absolute);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }

        public void StartBackgroundRecording()
        {
            try
            {
                Logger.Info("Requesting start of background recording services.");
                _recorderService.StartRecording(_settings);
                _audioManager.Start(_settings);
                ApplyVoiceTriggerSettings(); // start listening if enabled
                if (_toggleRecordMenuItem != null)
                {
                    _toggleRecordMenuItem.Header = "Stop Recording";
                }
                _mainWindow?.UpdateRecordingIndicator();
                _pluginManager?.RaiseRecordingStarted();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to start background recording.", ex);
            }
        }

        public void StopBackgroundRecording()
        {
            try
            {
                Logger.Info("Requesting stop of background recording services.");
                _recorderService.StopRecording();
                _audioManager.Stop();
                _voiceTrigger.Stop(); // stop listening when the buffer is off
                if (_toggleRecordMenuItem != null)
                {
                    _toggleRecordMenuItem.Header = "Start Recording";
                }
                _mainWindow?.UpdateRecordingIndicator();
                _pluginManager?.RaiseRecordingStopped();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to stop background recording.", ex);
            }
        }

        public bool VoiceTriggerRunning => _voiceTrigger != null && _voiceTrigger.IsRunning;

        /// <summary>Starts/stops the voice trigger to match the current settings + recording state.</summary>
        public void ApplyVoiceTriggerSettings()
        {
            try
            {
                if (_settings.VoiceTriggerEnabled && _recorderService.IsRecording)
                    _voiceTrigger.Start(_settings.VoiceTriggerPhrase, _settings.VoiceTriggerLanguage);
                else
                    _voiceTrigger.Stop();
            }
            catch (Exception ex)
            {
                Logger.Error("ApplyVoiceTriggerSettings error.", ex);
            }
        }

        /// <summary>
        /// Applies the current RecordingMode. "Auto" hands control to the game detector; "AlwaysOn"
        /// keeps the legacy always-running buffer; "Manual" leaves recording entirely to the user. Safe to
        /// call at startup and again whenever the mode changes in settings.
        /// </summary>
        public void ApplyRecordingModeSettings()
        {
            try
            {
                switch (_settings.RecordingMode)
                {
                    case "AlwaysOn":
                        _gameDetector.Stop();
                        if (!_recorderService.IsRecording) StartBackgroundRecording();
                        break;

                    case "Manual":
                        _gameDetector.Stop();
                        // Leave the current recording state as-is; the user drives it via tray/hotkey.
                        break;

                    default: // "Auto"
                        // The detector starts from an idle state, so if we were already recording (e.g. the
                        // user just switched away from Always-on) stop now and let detection decide afresh.
                        if (_recorderService.IsRecording && !_gameDetector.IsRunning) StopBackgroundRecording();
                        if (!_gameDetector.IsRunning) _gameDetector.Start();
                        break;
                }

                // Keep the tray toggle label in sync with the actual buffer state - in Auto/Manual we may
                // sit idle at startup without having called Start/StopBackgroundRecording.
                if (_toggleRecordMenuItem != null)
                    _toggleRecordMenuItem.Header = _recorderService.IsRecording ? "Stop Recording" : "Start Recording";
            }
            catch (Exception ex)
            {
                Logger.Error("ApplyRecordingModeSettings error.", ex);
            }
        }

        private void OnAutoRecordStart(string game)
        {
            if (!_recorderService.IsRecording) StartBackgroundRecording();
            _pluginManager?.RaiseGameDetected(game);
            if (_settings.AutoRecordNotify)
                ShowAutoRecordOverlay("RECORDING STARTED", string.IsNullOrWhiteSpace(game) ? "Game detected" : game, "●", "#FF4444");
        }

        private void OnAutoRecordStop()
        {
            if (_recorderService.IsRecording) StopBackgroundRecording();
            _pluginManager?.RaiseGameStopped();
            if (_settings.AutoRecordNotify)
                ShowAutoRecordOverlay("RECORDING STOPPED", "Instant Replay off - no game", "■", "#B9CACB");
        }

        /// <summary>
        /// The recording game moved to another monitor. In "Auto" record-monitor mode, rebuild the capture
        /// pipeline on the new display (a running FFmpeg input can't switch monitors live). No-op if we're
        /// not recording, the user pinned a specific monitor, or we're already capturing that display.
        /// </summary>
        private void OnGameMonitorChanged(string monitorKey)
        {
            try
            {
                if (!_recorderService.IsRecording) return;
                if (!string.Equals(_settings.RecordMonitor, "Auto", StringComparison.OrdinalIgnoreCase)) return;
                if (string.IsNullOrEmpty(monitorKey)) return;
                if (string.Equals(_recorderService.ActiveMonitorKey, monitorKey, StringComparison.OrdinalIgnoreCase)) return;

                Logger.Info($"Active game moved to monitor '{monitorKey}'; re-targeting capture from '{_recorderService.ActiveMonitorKey}'.");
                // Stop + start re-resolves the target monitor (Auto follows the now-foreground game window).
                StopBackgroundRecording();
                StartBackgroundRecording();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to re-target capture to the game's new monitor.", ex);
            }
        }

        /// <summary>
        /// Shows the click-through overlay popup for auto-record start/stop - the same window, positioning
        /// and no-focus-steal behavior as the "clip captured" notification. Must run on the UI thread.
        /// </summary>
        private void ShowAutoRecordOverlay(string title, string subtitle, string glyph, string colorHex)
        {
            try
            {
                var overlay = new NotificationOverlay();
                overlay.SetInfo(title, subtitle, glyph, colorHex);
                overlay.Show();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to display auto-record overlay.", ex);
            }
        }

        // ───────────── auto-update (Velopack + GitHub releases) ─────────────

        /// <summary>
        /// Kicks off a one-shot background check for a newer release on GitHub. If one is found it's
        /// downloaded and staged; we then surface a quiet overlay. The actual install happens on quit
        /// (ShutdownApp), so an in-progress recording is never interrupted. No-op for un-packaged dev runs.
        /// </summary>
        private void StartUpdateCheck()
        {
            try
            {
                _updateService = new UpdateService();
                if (!_updateService.IsInstalled) return; // dev / loose-copy run - nothing to update against

                Task.Run(async () =>
                {
                    string? version = await _updateService.CheckAndDownloadAsync();
                    if (version != null)
                        _ = Dispatcher.BeginInvoke(new Action(() => NotifyUpdateReady(version)));
                });
            }
            catch (Exception ex)
            {
                Logger.Warn($"StartUpdateCheck failed: {ex.Message}");
            }
        }

        /// <summary>Tray "Check for updates" handler: checks on demand and tells the user the outcome.</summary>
        private async void CheckForUpdatesManually()
        {
            try
            {
                _updateService ??= new UpdateService();
                if (!_updateService.IsInstalled)
                {
                    ShowAutoRecordOverlay("UPDATES", "Available only in the installed build", "↑", "#B9CACB");
                    return;
                }

                // Already downloaded this session - just remind them it's waiting.
                if (_updateService.PendingVersion is string pending)
                {
                    NotifyUpdateReady(pending);
                    return;
                }

                string? version = await _updateService.CheckAndDownloadAsync();
                if (version != null) NotifyUpdateReady(version);
                else ShowAutoRecordOverlay("UP TO DATE", "You're on the latest version of Wisp", "↑", "#B9CACB");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Manual update check failed: {ex.Message}");
            }
        }

        /// <summary>Quiet overlay telling the user a new version is downloaded and will install on quit.</summary>
        private void NotifyUpdateReady(string version)
        {
            ShowAutoRecordOverlay("UPDATE READY", $"Wisp {version} installs when you quit Wisp", "↑", "#3FB950");
        }

        private void ToggleRecording()
        {
            if (_recorderService.IsRecording)
            {
                StopBackgroundRecording();
                // In Auto mode a deliberate stop should stick - don't let the detector restart this game.
                if (_settings.RecordingMode == "Auto") _gameDetector.NotifyManuallyStopped();
            }
            else
            {
                StartBackgroundRecording();
                if (_settings.RecordingMode == "Auto") _gameDetector.NotifyManuallyStarted();
            }
        }

        /// <summary>
        /// Hotkey / voice-trigger entry point. With clip chaining on, taps are routed into the chain state
        /// machine (see App.Chaining.cs) so rapid taps stitch into one clip; with it off, each tap is a
        /// classic single save. The hook fires on a background thread, so chain handling is marshalled onto
        /// the UI thread where all chain state lives.
        /// </summary>
        private void OnHotkeyTriggered()
        {
            // This runs on the dedicated input-hook thread (or the voice-trigger thread). Do NO real work
            // here - hand it straight to the UI thread and return, so the hook callback never blocks. Using
            // BeginInvoke (non-blocking) means the actual capture - window creation, plugin fan-out, clip
            // assembly kickoff - runs on the UI thread's queue, serialized as before, while the hook thread
            // is free instantly. That's what keeps a mid-match clip tap from stalling game input.
            try
            {
                Dispatcher.BeginInvoke(new Action(HandleHotkeyOnUi));
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to marshal hotkey handling to the UI thread.", ex);
            }
        }

        /// <summary>The actual hotkey response, always on the UI thread: plugin fan-out, then chain or single capture.</summary>
        private void HandleHotkeyOnUi()
        {
            if (!_recorderService.IsRecording) return;

            _pluginManager?.RaiseHotkey();

            if (_settings.ClipChainingEnabled)
            {
                HandleChainTap();
                return;
            }

            RunSingleClipCapture();
        }

        /// <summary>The classic single-tap capture: assemble the last buffer seconds into one clip.</summary>
        private async void RunSingleClipCapture()
        {
            if (!_recorderService.IsRecording) return;

            if (_isSavingClip)
            {
                Logger.Warn("Hotkey triggered while another clip saving operation is in progress. Ignoring.");
                return;
            }

            _isSavingClip = true;

            // Capture the focused game/app now, before the notification overlay can take foreground.
            string gameName = FFmpegRecorderService.GetForegroundGameName();

            // Progress shown (default): pop the "clipping…" overlay now and update it in place when the
            // save finishes. Progress hidden: give instant optimistic feedback - show the "Clip captured"
            // popup + chime immediately and let the save run in the background - then stay quiet unless it
            // actually fails, in which case we surface a "not saved" popup later to correct it.
            bool showProgress = _settings.ShowCaptureProgress;
            NotificationOverlay? overlay = null;
            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (showProgress)
                    {
                        overlay = new NotificationOverlay();
                        overlay.Show();
                    }
                    else
                    {
                        ShowResultOnlyNotification(true, ""); // instant "Clip captured" + chime
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to display capture notification.", ex);
                }
            });

            try
            {
                // Run saving process in the background
                var clip = await _recorderService.SaveClipAsync(_settings, _audioManager, gameName);

                // Update notification and UI on the UI thread
                Dispatcher.Invoke(() =>
                {
                    if (clip != null)
                    {
                        // Success: in progress mode flip the spinner to success; in instant mode the
                        // optimistic popup already said so, so there's nothing more to show.
                        if (showProgress) overlay?.UpdateToSuccess();
                        _mainWindow?.LoadClips();
                        ShowToastNotification(clip);
                    }
                    else
                    {
                        // The save failed - correct the in-progress spinner, or (instant mode) raise a
                        // "not saved" popup now since the optimistic one wrongly said it succeeded.
                        if (showProgress) overlay?.UpdateToFailure("Failed to assemble clip.");
                        else ShowResultOnlyNotification(false, "Clip could not be saved.");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Exception while saving clip via hotkey.", ex);
                Dispatcher.Invoke(() =>
                {
                    if (showProgress) overlay?.UpdateToFailure("Save error occurred.");
                    else ShowResultOnlyNotification(false, "Clip could not be saved.");
                });
            }
            finally
            {
                _isSavingClip = false;
            }
        }

        /// <summary>
        /// Shows a single result-only capture popup (no loading phase) - used when the user has turned
        /// off "show capture progress". Must be called on the UI thread.
        /// </summary>
        private void ShowResultOnlyNotification(bool success, string reason)
        {
            try
            {
                var overlay = new NotificationOverlay(resultMode: true);
                overlay.SetResult(success, reason);
                overlay.Show();
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to display result-only notification overlay.", ex);
            }
        }

        public void ShowMainWindow()
        {
            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow();
                _mainWindow.Closed += (s, e) => _mainWindow = null;
            }
            
            _mainWindow.Show();
            _mainWindow.Activate();
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
        }

        public void ShowSettingsWindow()
        {
            ShowMainWindow();
            _mainWindow?.ShowSettingsTab();
        }

        /// <summary>
        /// Applies the user's auto-deletion limits (age limit + size budget) off the UI thread and reloads
        /// the library if anything changed. No-op when both limits are off. Favorited / protected clips
        /// (Clip.IsKept) are never removed. Called at startup, after each saved clip, and when the Storage
        /// tab's config is applied.
        /// </summary>
        public void RunAutoDeletionSweep()
        {
            var settings = _settings;
            if (settings == null || _dbService == null) return;
            if (!settings.RetentionEnabled && !settings.MaxStorageEnabled) return;

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (settings.RetentionEnabled && settings.RetentionDays > 0)
                        _dbService.CleanupOldClips(settings.RetentionDays);
                    if (settings.MaxStorageEnabled)
                        _dbService.EnforceStorageCap(GbToBytes(settings.MaxStorageGB));
                }
                catch (Exception ex)
                {
                    Logger.Error("Auto-deletion sweep error.", ex);
                }
                RunOnUi(() => _mainWindow?.LoadClips());
            });
        }

        /// <summary>Converts a gigabyte budget to bytes (binary GiB, matching how usage is displayed).</summary>
        internal static long GbToBytes(double gb) => (long)(Math.Max(0, gb) * 1024.0 * 1024.0 * 1024.0);

        private void ShowToastNotification(Clip clip)
        {
            // A clip is final + user-facing at every call site of this method (normal capture, lone
            // chain tap, and stitched chain), so this is the single place to tell plugins it was saved.
            _pluginManager?.RaiseClipSaved(clip);

            // Keep the library within the user's auto-deletion limits now that a new clip exists (the size
            // budget mainly). Runs off the UI thread and reloads the library only if anything is removed.
            RunAutoDeletionSweep();

            try
            {
                string xml = $@"
                    <toast>
                        <visual>
                            <binding template='ToastGeneric'>
                                <text>Clip saved ({clip.FormattedDuration})</text>
                                <text>{clip.Filename}</text>
                            </binding>
                        </visual>
                    </toast>";

                var xmlDoc = new global::Windows.Data.Xml.Dom.XmlDocument();
                xmlDoc.LoadXml(xml);
                var toast = new global::Windows.UI.Notifications.ToastNotification(xmlDoc);
                global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier("Wisp").Show(toast);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to show toast notification: {ex.Message}");
                // Toast notifications may fail without AppUserModelId registration - non-fatal
            }
        }

        public void ShutdownApp()
        {
            try { _idleTrimTimer?.Stop(); } catch { }
            ShutdownPlugins();
            _gameDetector?.Stop();
            _killDetection?.Dispose();
            StopBackgroundRecording();
            _hookManager?.Dispose();
            _voiceTrigger?.Dispose();
            _taskbarIcon?.Dispose();
            
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
            _singleInstanceMutex?.Dispose();

            // If a newer release was downloaded this session, let Velopack swap it in now that we're
            // exiting (it waits for this process to die, applies the files, and does NOT relaunch - the
            // user gets the new version next time they open Wisp). No-op when nothing was staged.
            _updateService?.ApplyPendingOnExit();

            // Force exit process
            Environment.Exit(0);
        }
    }
}
