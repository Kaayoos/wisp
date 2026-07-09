using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Wisp.Services;

namespace Wisp
{
    public partial class NotificationOverlay : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TRANSPARENT = 0x00000020;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private Storyboard? _spinStoryboard;

        // Result-only mode (no loading spinner): the popup appears already showing the final outcome.
        // Used when the user has turned off "show capture progress".
        private readonly bool _resultMode;
        private bool _resultSuccess;

        // Info mode: a generic, silent message (e.g. auto-record started/stopped) - no spinner, no chime,
        // appears already in its final state and auto-dismisses like the result popup.
        private bool _infoMode;

        private static readonly System.Windows.Media.Color ChainAccentFallback = System.Windows.Media.Color.FromRgb(0x00, 0xF2, 0xFF);

        public NotificationOverlay(bool resultMode = false)
        {
            InitializeComponent();
            _resultMode = resultMode;
            this.Opacity = 0; // Start hidden, faded in by IntroStoryboard. Positioned in Window_Loaded.
        }

        /// <summary>
        /// Paints the final result (success/failure) immediately - call this before Show() so a
        /// result-only popup never flashes the spinner. The chime and auto-dismiss are driven from
        /// Window_Loaded so the entrance animation and a single opacity timeline stay intact.
        /// </summary>
        public void SetResult(bool success, string reason = "")
        {
            _resultSuccess = success;

            Spinner.Visibility = Visibility.Collapsed;
            if (success)
            {
                SuccessIcon.Visibility = Visibility.Visible;
                FailureIcon.Visibility = Visibility.Collapsed;
                TitleText.Text = "CLIP CAPTURED";
                TitleText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136));
                SubText.Text = "Saved successfully!";
                NotificationBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136));
            }
            else
            {
                FailureIcon.Visibility = Visibility.Visible;
                SuccessIcon.Visibility = Visibility.Collapsed;
                TitleText.Text = "CLIPPING FAILED";
                TitleText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 68, 68));
                SubText.Text = reason;
                NotificationBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 68, 68));
            }
        }

        /// <summary>
        /// Paints a generic info message (used for auto-record started/stopped) and switches the popup into
        /// silent, no-spinner, auto-dismiss mode. Call before Show().
        /// </summary>
        public void SetInfo(string title, string subtitle, string glyph, string colorHex)
        {
            _infoMode = true;
            var brush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colorHex));

            Spinner.Visibility = Visibility.Collapsed;
            SuccessIcon.Visibility = Visibility.Collapsed;
            FailureIcon.Visibility = Visibility.Collapsed;
            InfoIcon.Text = glyph;
            InfoIcon.Foreground = brush;
            InfoIcon.Visibility = Visibility.Visible;

            TitleText.Text = title;
            TitleText.Foreground = brush;
            SubText.Text = subtitle;
            NotificationBorder.BorderBrush = brush;
        }

        // Places the popup at an absolute physical-pixel spot on the chosen monitor's work area. Done in
        // physical pixels (via SetWindowPos) rather than WPF's Left/Top, because under per-monitor DPI the
        // DIP↔pixel mapping changes as a window crosses monitors - so DIP math mis-placed the toast on a
        // second monitor whose scaling differed from the primary.
        private void ApplyPositioning()
        {
            try
            {
                var settings = ((App)Application.Current).Settings;
                var screens = System.Windows.Forms.Screen.AllScreens;

                int monitorIndex = settings.NotificationMonitorIndex;
                if (monitorIndex < 0 || monitorIndex >= screens.Length) monitorIndex = 0;

                var screen = screens[monitorIndex];
                var wa = screen.WorkingArea; // physical px
                double scale = DisplayHelper.GetDpiScaleForScreen(screen);

                double widthDip = double.IsNaN(this.Width) ? 240 : this.Width;
                double heightDip = double.IsNaN(this.Height) ? 52 : this.Height;
                int physW = (int)Math.Round(widthDip * scale);
                int physH = (int)Math.Round(heightDip * scale);

                int x = wa.Left + (int)Math.Round(settings.NotificationPosPctX * (wa.Width - physW));
                int y = wa.Top + (int)Math.Round(settings.NotificationPosPctY * (wa.Height - physH));

                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    DisplayHelper.MoveWindowPhysical(hwnd, x, y);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Notification positioning failed: {ex.Message}");
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // Inject Win32 styles to make the window click-through and prevent focus theft
            try
            {
                var helper = new WindowInteropHelper(this);
                int exStyle = GetWindowLong(helper.Handle, GWL_EXSTYLE);
                SetWindowLong(helper.Handle, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to set notification window styles: {ex.Message}");
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Position now (handle exists, WPF has done its initial placement, window still at Opacity 0).
            ApplyPositioning();

            // Fight for the top of the z-order so the toast shows over composited fullscreen games
            // (flip-model / Windows Fullscreen Optimizations), where a set-once Topmost can be buried the
            // instant the game re-asserts its placement. Re-asserted briefly across the toast's life. This
            // is a no-op over true exclusive fullscreen (nothing external can draw there) - see ForceTopMost.
            StartTopmostReassert();

            // Slide + fade the popup in.
            var intro = this.FindResource("IntroStoryboard") as Storyboard;
            intro?.Begin(this);

            if (_resultMode)
            {
                // Result-only: no spinner. Play the outcome sound now (SetResult already painted the
                // final state), then run the hold + fade-out once the intro has settled. Starting the
                // outro a beat later avoids it fighting the intro over the window's Opacity - whichever
                // Begin ran last would otherwise win and break the auto-dismiss.
                if (_resultSuccess) SoundManager.PlaySuccessSound(((App)Application.Current).Settings);
                else SoundManager.PlayFailureSound(((App)Application.Current).Settings);

                var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                settle.Tick += (s, ev) =>
                {
                    settle.Stop();
                    (this.FindResource("OutroStoryboard") as Storyboard)?.Begin(this);
                };
                settle.Start();
                return;
            }

            if (_infoMode)
            {
                // Same gentle settle-then-fade as the result popup, but silent - no chime on every game.
                var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                settle.Tick += (s, ev) =>
                {
                    settle.Stop();
                    (this.FindResource("OutroStoryboard") as Storyboard)?.Begin(this);
                };
                settle.Start();
                return;
            }

            _spinStoryboard = this.FindResource("SpinStoryboard") as Storyboard;
            _spinStoryboard?.Begin(this, true); // Use controllable=true so we can stop/manipulate it
        }

        public void UpdateToSuccess()
        {
            Dispatcher.Invoke(() =>
            {
                // Play success chime
                SoundManager.PlaySuccessSound(((App)Application.Current).Settings);

                // Stop spinning
                _spinStoryboard?.Stop(this);
                Spinner.Visibility = Visibility.Collapsed;

                // Show Success icon
                SuccessIcon.Visibility = Visibility.Visible;

                // Update text
                TitleText.Text = "CLIP CAPTURED";
                TitleText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136)); // Cyan/Green glow
                SubText.Text = "Saved successfully!";

                // Pulse the border slightly or change color to celebrate success
                NotificationBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 255, 136));

                // Start Outro (fades out after hold time)
                var outro = this.FindResource("OutroStoryboard") as Storyboard;
                outro?.Begin(this);
            });
        }

        public void UpdateToFailure(string reason)
        {
            Dispatcher.Invoke(() =>
            {
                // Play failure buzzer
                SoundManager.PlayFailureSound(((App)Application.Current).Settings);

                // Stop spinning
                _spinStoryboard?.Stop(this);
                Spinner.Visibility = Visibility.Collapsed;

                // Show Warning icon
                FailureIcon.Visibility = Visibility.Visible;

                // Update text
                TitleText.Text = "CLIPPING FAILED";
                TitleText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 68, 68)); // Red error
                SubText.Text = reason;

                // Update border color to red
                NotificationBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 68, 68));

                // Start Outro (fades out after hold time)
                var outro = this.FindResource("OutroStoryboard") as Storyboard;
                outro?.Begin(this);
            });
        }

        // ===================== Clip chaining =====================

        /// <summary>Resolves the live accent color (falls back to Wisp cyan if the resource is missing).</summary>
        private static System.Windows.Media.Color AccentColor()
        {
            try
            {
                if (Application.Current?.Resources["AccentColor"] is System.Windows.Media.Color c) return c;
                if (Application.Current?.Resources["AccentBrush"] is System.Windows.Media.SolidColorBrush b) return b.Color;
            }
            catch { }
            return ChainAccentFallback;
        }

        /// <summary>
        /// Paints a brief "chained ×N" popup and auto-dismisses it (same silent settle-then-fade as the
        /// info popup). Shown for each tap that extends a chain, so the user sees that this is the Nth tap
        /// stitched together - no countdown, no lingering. Call before Show().
        /// </summary>
        public void SetChainCount(int count)
        {
            _infoMode = true; // reuse the silent, auto-dismissing path in Window_Loaded
            var accent = new System.Windows.Media.SolidColorBrush(AccentColor());

            Spinner.Visibility = Visibility.Collapsed;
            SuccessIcon.Visibility = Visibility.Collapsed;
            FailureIcon.Visibility = Visibility.Collapsed;
            InfoIcon.Visibility = Visibility.Collapsed;
            ChainIcon.Visibility = Visibility.Visible;
            ChainIcon.Foreground = accent;

            TitleText.Text = "CLIPPING…";
            TitleText.Foreground = accent;
            SubText.Text = $"Chained ×{count}";
            NotificationBorder.BorderBrush = accent;
        }

        private System.Windows.Threading.DispatcherTimer? _topmostTimer;

        /// <summary>
        /// Pushes the toast to the top of the z-order now and re-asserts it a handful of times over its
        /// on-screen life, so a composited fullscreen game can't bury it a frame after we appear. Uses
        /// SWP_NOACTIVATE (via ForceTopMost) so the game never loses focus. Auto-stops; also stopped in OnClosed.
        /// </summary>
        private void StartTopmostReassert()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            DisplayHelper.ForceTopMost(hwnd);

            int ticks = 0;
            _topmostTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _topmostTimer.Tick += (s, e) =>
            {
                DisplayHelper.ForceTopMost(hwnd);
                if (++ticks >= 12) { _topmostTimer?.Stop(); _topmostTimer = null; } // ~3s covers show+hold+fade
            };
            _topmostTimer.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            _topmostTimer?.Stop();
            _topmostTimer = null;
            base.OnClosed(e);
        }

        private void OutroStoryboard_Completed(object sender, EventArgs e)
        {
            this.Close();
        }
    }
}

