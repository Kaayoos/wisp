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
using Wisp.Services;

namespace Wisp
{
    public partial class MainWindow : Window
    {
        private readonly App _app;
        public Wisp.Controls.TargetCursor TargetCursor { get; private set; }

        /// <summary>App version for UI labels (e.g. "1.0.0"), read from the running assembly so it always
        /// reflects the installed/auto-updated build - nothing to hand-edit each release.</summary>
        public string AppVersionText
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
            }
        }
        private List<Clip> _allClips = new();
        private DispatcherTimer? _captureTimer;
        private DispatcherTimer? _toastTimer;

        // Game library filter
        private const string AllGamesLabel = "All Games";
        private const string AllTagsLabel = "All Tags";
        private bool _suppressGameFilter;
        private bool _suppressTagFilter;
        private bool _showOnlyFavorites = false;

        // Video Player State
        private DispatcherTimer? _playerTimer;
        private bool _isUserSeeking = false;
        private bool _isPlaying = false;
        private double _lastVolume = 0.8;
        private bool _isMuted = false;

        // Live audio mixer (separate system/mic tracks) + waveform
        private readonly ClipAudioMixer _audioMixer = new();
        private bool _mixerActive;
        private bool _suppressMixEvents;
        private System.Windows.Shapes.Rectangle? _playhead;
        private int _waveformToken;
        // One visual lane per captured source (mic / system / social), or a single combined lane for
        // legacy clips with no separate tracks. Each lane carries its own peaks, color and bars so the
        // playhead can recolor "played" portions per lane.
        private readonly List<WaveLane> _laneData = new();
        private bool _scrubWasPlaying;
        private long _lastScrubTicks;
        // Right-click-a-lane volume popup: which mixer source it currently targets ("MIC"/"SYSTEM"/"SOCIAL"),
        // and a guard so syncing its controls open doesn't echo back into the live mixer.
        private string _activeLaneSource = "";
        private bool _suppressLaneEvents;

        // Settings Rebinding State
        private List<int> _reboundKeys = new();
        private string _hotkeyText = "";
        private bool _isRebinding = false;

        // Appearance / theming - accent is previewed live, persisted on Save, reverted on Discard.
        private string _pendingThemePreset = "Wisp Cyan";
        private string _pendingAccentHex = "";
        private string _pendingActiveThemeId = ""; // selected full theme (built-in/plugin); "" = default look (accent-only)
        private bool _suppressAppearanceEvents;
        private static readonly SolidColorBrush AppearanceRingBrush = Frozen(0xF2, 0xF2, 0xF4, 1.0);

        // Pending Notification Position coordinates
        private double _pendingNotifPosPctX = 0.95;
        private double _pendingNotifPosPctY = 0.05;

        // Export & Trim State
        private Clip? _activeClip;
        private double _trimStartFrac = 0.0;
        private double _trimEndFrac = 1.0;
        private bool _isExportSidebarOpen = false;
        private string _exportedClipPath = "";
        private double _trimmedLosslessMB; // ~size of a lossless copy of the CURRENT trim region
        private bool _dragOccurred = false;
        private Point _dragStartPoint;

        // Cropped-aspect export (Vertical 9:16 or a Custom W:H). _verticalExport means "a crop is active"
        // (true for both the Vertical and Custom chips); _customAspect distinguishes the Custom chip; and
        // _exportAspect is the crop window's width/height. _vCropFracX/Y position that window inside the
        // frame's free space (0 = top/left, 1 = bottom/right); only the axis with slack actually moves. The
        // same fractions + aspect drive both the on-video preview frame and the ffmpeg crop on export.
        private bool _verticalExport = false;
        private bool _customAspect = false;
        private double _exportAspect = 9.0 / 16.0;
        private double _vCropFracX = 0.5;
        private double _vCropFracY = 0.5;
        private bool _cropDragging = false;
        private Point _cropDragStart;
        private double _cropStartFracX, _cropStartFracY;

        public static readonly DependencyProperty ClipItemWidthProperty =
            DependencyProperty.Register(nameof(ClipItemWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(248.0));

        public double ClipItemWidth
        {
            get => (double)GetValue(ClipItemWidthProperty);
            set => SetValue(ClipItemWidthProperty, value);
        }

        private void ClipsListBox_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateClipItemWidth();
        }

        private void UpdateClipItemWidth()
        {
            double width = ClipsListBox.ActualWidth;
            if (width <= 0) return;

            // Subtract 24 to account for scrollbar + safety margin to prevent unwanted wrapping/scrollbars.
            double availableWidth = width - 24;

            // Min width of ListBoxItem = 220 (card base min width) + 28 (margins/paddings) = 248.
            double minItemWidth = 248;

            int columns = (int)Math.Floor(availableWidth / minItemWidth);
            if (columns < 1) columns = 1;

            double itemWidth = availableWidth / columns;
            ClipItemWidth = itemWidth;
        }

        public MainWindow()
        {
            InitializeComponent();
            _app = (App)Application.Current;
            TargetCursor = new Wisp.Controls.TargetCursor(this);
            ClipsListBox.SizeChanged += ClipsListBox_SizeChanged;
            this.Loaded += (s, e) => { if (_app.Settings.TargetCursorEnabled) TargetCursor.Enable(); };
            
            // Initialize hotkey state fields
            _reboundKeys = _app.Settings.GetHotkeyKeysList();
            _hotkeyText = _app.Settings.HotkeyText;
            
            // Set hotkey badge text in sidebar
            HotkeyBadge.Text = _hotkeyText;

            // Keep the title-bar maximize/restore icon in sync with the window state.
            this.StateChanged += (s, e) => UpdateMaximizeIcon();

            // Load records
            LoadClips();
            UpdateRecordingIndicator();

            // Load settings into the integrated settings panel
            LoadCurrentSettings();

            // Start polling foreground window for the capture indicator
            _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _captureTimer.Tick += (s, e) => UpdateCaptureTarget();
            _captureTimer.Start();
            UpdateCaptureTarget();

            // When hidden to the tray, tear down the player and release the gallery's decoded
            // thumbnails so the background footprint drops. OnActivated reloads them on reopen.
            this.IsVisibleChanged += (s, e) =>
            {
                if (!this.IsVisible)
                {
                    ClosePlayer();
                    ClipsListBox.ItemsSource = null;
                    _allClips = new();
                    // Stop the capture-console pulse while hidden so the background process keeps a
                    // truly flat CPU profile; OnActivated → UpdateRecordingIndicator restarts it on reopen.
                    BufferStatusDot?.BeginAnimation(UIElement.OpacityProperty, null);
                    TrimMemory();
                }
            };
        }

        [System.Runtime.InteropServices.DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hProcess);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
        private static extern IntPtr GetCurrentProcessPseudoHandle();

        /// <summary>
        /// Reclaims managed memory and trims the working set. Called when the window goes to the
        /// tray (not a hot path), so the background process actually gives RAM back to the OS
        /// instead of sitting on an inflated heap.
        /// </summary>
        private static void TrimMemory()
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                EmptyWorkingSet(GetCurrentProcessPseudoHandle());
            }
            catch { }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            LoadClips();
            UpdateRecordingIndicator();
            HotkeyBadge.Text = _hotkeyText;
        }

        // ================= NAVIGATION TABS (StitchMCP UI) =================
        // The player overlay lives in the content cell and does NOT cover the left nav rail, so the
        // tabs stay clickable while a clip is open - clicking one used to silently switch the hidden
        // tab underneath. Returns false to swallow the click (export in progress); otherwise closes
        // the player first so the chosen tab is actually visible.
        private bool LeavePlayerForTabSwitch()
        {
            if (ExportProgressOverlay != null && ExportProgressOverlay.Visibility == Visibility.Visible)
                return false; // export running - don't navigate out from under it
            if (PlayerOverlay.Visibility == Visibility.Visible)
                ClosePlayer();
            return true;
        }

        private void LibraryTab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!LeavePlayerForTabSwitch()) return;
            if (!ConfirmDiscardSettingsChanges()) return;
            SwitchToLibraryTab();
        }

        private void FavoritesTab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!LeavePlayerForTabSwitch()) return;
            if (!ConfirmDiscardSettingsChanges()) return;
            SwitchToFavoritesTab();
        }

        private void SettingsTab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!LeavePlayerForTabSwitch()) return;
            SwitchToSettingsTab();
        }

        private void HotkeyBadgeBorder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                StartHotkeyRebinding(true);
            }
        }

        private void SupportTab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://discord.com/invite/gR5DqCdGre",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open support link: {ex.Message}");
            }
        }

        // Opens external credit/license links from the About & Credits card in the default browser.
        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open link: {ex.Message}");
            }
            e.Handled = true;
        }

        // Opens the NOTICES.txt that ships next to the executable.
        private void OpenNotices_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NOTICES.txt");
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                else
                    CustomMessageBox.Show("NOTICES.txt was not found next to the application.", "Notices", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open notices: {ex.Message}");
            }
        }

        private void FadeInElement(FrameworkElement element)
        {
            element.Visibility = Visibility.Visible;
            var anim = new DoubleAnimation
            {
                From = 0.0,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromSeconds(0.25)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            element.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // ================= IN-APP TOAST =================
        // A transient, non-blocking confirmation. Used for purely informational results
        // ("saved", "refreshed", "copied") so the UI never stalls on a modal OK button.
        public enum ToastKind { Success, Info, Warning, Error }

        public void ShowToast(string title, string? message = null, ToastKind kind = ToastKind.Success)
        {
            if (ToastHost == null) return;

            ToastTitle.Text = title;
            if (string.IsNullOrWhiteSpace(message))
            {
                ToastMessage.Visibility = Visibility.Collapsed;
            }
            else
            {
                ToastMessage.Text = message;
                ToastMessage.Visibility = Visibility.Visible;
            }

            (string glyph, string color) = kind switch
            {
                ToastKind.Warning => ("", "#FFC53D"),
                ToastKind.Error   => ("", "#FF4D4D"),
                ToastKind.Info    => ("", ThemeManager.DefaultAccentHex), // overridden to the live accent in ShowToast
                _                 => ("", "#34D399"), // Success
            };
            // Info follows the user's accent (which they may have re-themed); others keep their hue.
            SolidColorBrush brush = kind == ToastKind.Info
                ? ThemeManager.AccentBrush
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            ToastIcon.Text = glyph;
            ToastIcon.Foreground = brush;
            ToastHost.BorderBrush = brush;

            // Restart cleanly if a toast is already on screen.
            _toastTimer?.Stop();
            ToastHost.Visibility = Visibility.Visible;

            ToastHost.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromSeconds(0.28)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            ToastTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, TimeSpan.FromSeconds(0.34)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } });

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer?.Stop();
                HideToast();
            };
            _toastTimer.Start();
        }

        private void HideToast()
        {
            if (ToastHost == null || ToastHost.Visibility != Visibility.Visible) return;
            var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.25)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            fade.Completed += (s, e) => { if (ToastHost != null) ToastHost.Visibility = Visibility.Collapsed; };
            ToastHost.BeginAnimation(UIElement.OpacityProperty, fade);
            ToastTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(24, TimeSpan.FromSeconds(0.25)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        }

        // Focuses the library search box (Ctrl+K). Search only exists on the library/favorites
        // views, so switch there first when it's safe to (no unsaved settings, no open player).
        private void FocusSearch()
        {
            if (PlayerOverlay.Visibility == Visibility.Visible) return;

            if (SettingsGrid.Visibility == Visibility.Visible)
            {
                if (HasUnsavedSettingsChanges()) return; // don't yank the user out of unsaved edits
                SwitchToLibraryTab();
            }

            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
            PulseSearchBox();
        }

        // Quick cyan flash on the search field's border so Ctrl+K gives visible feedback.
        private void PulseSearchBox()
        {
            if (SearchBorder?.BorderBrush is SolidColorBrush brush)
            {
                try
                {
                    var anim = new ColorAnimation
                    {
                        To = ThemeManager.AccentColor,
                        Duration = TimeSpan.FromSeconds(0.18),
                        AutoReverse = true,
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };
                    brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                }
                catch { /* frozen brush - focus still worked, the flash is just cosmetic */ }
            }
        }

        // Collapses every page + header and clears all tab highlights. Each SwitchTo* calls this first,
        // then reveals only its own view - so a page can never linger over another page after you
        // navigate away. Add a new tab? Hide it here once and every switch handles it.
        private void ResetAllViews()
        {
            LibraryTab.Tag = "";
            FavoritesTab.Tag = "";
            SettingsTab.Tag = "";
            PluginsTab.Tag = "";
            StorageTab.Tag = "";

            LibraryHeader.Visibility = Visibility.Collapsed;
            SettingsHeader.Visibility = Visibility.Collapsed;
            PluginsHeader.Visibility = Visibility.Collapsed;
            StorageHeader.Visibility = Visibility.Collapsed;

            ClipLibraryGrid.Visibility = Visibility.Collapsed;
            ClipLibraryGrid.Opacity = 0;
            SettingsGrid.Visibility = Visibility.Collapsed;
            SettingsGrid.Opacity = 0;
            PluginsGrid.Visibility = Visibility.Collapsed;
            PluginsGrid.Opacity = 0;
            StorageGrid.Visibility = Visibility.Collapsed;
            StorageGrid.Opacity = 0;
        }

        public void SwitchToLibraryTab()
        {
            _showOnlyFavorites = false;
            ResetAllViews();

            LibraryTab.Tag = "Selected";
            LibraryHeader.Visibility = Visibility.Visible;
            HeaderTitleText.Text = "Recent Captures";
            HeaderSubtitleText.Text = "Your latest highlights and recordings.";

            ApplyFilterAndSort();
            FadeInElement(ClipLibraryGrid);
        }

        public void SwitchToFavoritesTab()
        {
            _showOnlyFavorites = true;
            ResetAllViews();

            FavoritesTab.Tag = "Selected";
            LibraryHeader.Visibility = Visibility.Visible;
            HeaderTitleText.Text = "Favorite Captures";
            HeaderSubtitleText.Text = "Your most prized highlights and clips.";

            ApplyFilterAndSort();
            FadeInElement(ClipLibraryGrid);
        }

        public void SwitchToSettingsTab()
        {
            ResetAllViews();

            SettingsTab.Tag = "Selected";
            SettingsHeader.Visibility = Visibility.Visible;

            FadeInElement(SettingsGrid);

            // Load fresh settings values
            LoadCurrentSettings();
        }

        public void ShowSettingsTab()
        {
            SwitchToSettingsTab();
        }

        // ================= WINDOW LIFECYCLE =================
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.ClickCount == 2)
            {
                ToggleMaximize();
                return;
            }
            if (this.WindowState == WindowState.Maximized) return; // can't drag a maximized window
            try { this.DragMove(); } catch { }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            this.WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            // Close hides the window to tray instead of exiting
            this.Hide();
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

        private void ToggleMaximize()
        {
            this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void UpdateMaximizeIcon()
        {
            bool max = this.WindowState == WindowState.Maximized;
            if (MaximizeIcon != null) MaximizeIcon.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
            if (RestoreIcon != null) RestoreIcon.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
            if (MaximizeButton != null) MaximizeButton.ToolTip = max ? "Restore" : "Maximize";
        }

        // A borderless (WindowStyle=None) window maximizes over the taskbar by default; constrain the
        // maximized size to the monitor's work area so the taskbar stays visible and it lands on the
        // right monitor.
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            System.Windows.Interop.HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
        }

        private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                IntPtr monitor = MonitorFromWindow(hwnd, 0x00000002 /* MONITOR_DEFAULTTONEAREST */);
                if (monitor != IntPtr.Zero)
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                        RECT work = info.rcWork;
                        RECT mon = info.rcMonitor;
                        mmi.ptMaxPosition.x = work.left - mon.left;
                        mmi.ptMaxPosition.y = work.top - mon.top;
                        mmi.ptMaxSize.x = work.right - work.left;
                        mmi.ptMaxSize.y = work.bottom - work.top;
                        // Raise the max TRACK size too, or WPF's default (sized to the primary monitor) caps
                        // the maximized window so it can't fill a larger second monitor.
                        mmi.ptMaxTrackSize.x = work.right - work.left;
                        mmi.ptMaxTrackSize.y = work.bottom - work.top;
                        // Because we mark the message handled below, WPF no longer enforces the window's
                        // MinWidth/MinHeight through it - so set the minimum track size ourselves (in physical
                        // pixels for this monitor's DPI), or the CanResizeWithGrip border could drag smaller
                        // than the 1040×640 minimum.
                        try
                        {
                            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                            mmi.ptMinTrackSize.x = (int)Math.Ceiling(this.MinWidth * dpi.DpiScaleX);
                            mmi.ptMinTrackSize.y = (int)Math.Ceiling(this.MinHeight * dpi.DpiScaleY);
                        }
                        catch { /* keep the OS default minimum if DPI isn't resolvable yet */ }
                        Marshal.StructureToPtr(mmi, lParam, true);
                        // CRITICAL: mark the message handled so WPF's *own* WM_GETMINMAXINFO handler doesn't
                        // run after us and overwrite these values. That override is multi-monitor-buggy - it
                        // matches the process/system DPI (the primary), so it looks right on the primary but
                        // makes "fullscreen"/maximize spill off a differently-scaled (e.g. bigger) second
                        // monitor with ~¼ of the window off-screen. Letting our physical-pixel work-area
                        // bounds win fixes that on every display.
                        handled = true;
                    }
                }
            }
            return IntPtr.Zero;
        }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)] private struct POINTSTRUCT { public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINTSTRUCT ptReserved; public POINTSTRUCT ptMaxSize; public POINTSTRUCT ptMaxPosition; public POINTSTRUCT ptMinTrackSize; public POINTSTRUCT ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

        // ================= ACTIONS & BUTTONS =================
        private void ToggleRecBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_app.RecorderService.IsRecording)
            {
                _app.StopBackgroundRecording();
            }
            else
            {
                _app.StartBackgroundRecording();
            }
        }
        // ================= KEYBOARD HANDLING =================
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+K - jump to the library search box.
            if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                FocusSearch();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                // Close the player overlay; else restore a maximized window; else hide to tray.
                if (PlayerOverlay.Visibility == Visibility.Visible)
                {
                    ClosePlayer();
                }
                else if (this.WindowState == WindowState.Maximized)
                {
                    this.WindowState = WindowState.Normal;
                }
                else
                {
                    this.Hide();
                }
                e.Handled = true;
            }
            else if (PlayerOverlay.Visibility == Visibility.Visible)
            {
                if (e.Key == Key.Space)
                {
                    TogglePlayPause();
                    e.Handled = true;
                }
                else if (e.Key == Key.Left)
                {
                    StepFrame(-1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Right)
                {
                    StepFrame(1);
                    e.Handled = true;
                }
                else if (e.Key == Key.F)
                {
                    ToggleMaximize();
                    e.Handled = true;
                }
            }
        }
    }
}