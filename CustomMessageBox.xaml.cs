using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wisp.Services;

namespace Wisp
{
    public partial class CustomMessageBox : Window
    {
        private MessageBoxResult _result = MessageBoxResult.None;

        private CustomMessageBox(string message, string title, MessageBoxButton button, MessageBoxImage image)
        {
            InitializeComponent();
            
            TitleText.Text = (title ?? "NOTIFICATION").ToUpperInvariant();
            MessageText.Text = message;

            // Map image icon
            switch (image)
            {
                case MessageBoxImage.Warning:
                    IconText.Text = "\uE7BA"; // Warning triangle
                    IconText.Foreground = Brushes.Tomato;
                    break;
                case MessageBoxImage.Error:
                    IconText.Text = "\uEA39"; // Error badge
                    IconText.Foreground = Brushes.Tomato;
                    break;
                case MessageBoxImage.Question:
                    IconText.Text = "\uE9CE"; // Help/Question
                    IconText.Foreground = ThemeManager.AccentBrush;
                    break;
                default: // Information
                    IconText.Text = "\uE946"; // Info circle
                    IconText.Foreground = ThemeManager.AccentBrush;
                    break;
            }

            // Setup buttons
            if (button == MessageBoxButton.YesNo)
            {
                YesBtn.Content = "Yes";
                NoBtn.Content = "No";
                NoBtn.Visibility = Visibility.Visible;
            }
            else
            {
                YesBtn.Content = "OK";
                NoBtn.Visibility = Visibility.Collapsed;
            }
        }

        public static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton button, MessageBoxImage image)
        {
            var box = new CustomMessageBox(message, title, button, image);
            if (owner != null && owner.IsVisible)
            {
                box.Owner = owner;
            }
            box.ShowDialog();
            return box._result;
        }

        public static MessageBoxResult Show(string message, string title, MessageBoxButton button, MessageBoxImage image)
        {
            var owner = Application.Current.MainWindow;
            return Show(owner, message, title, button, image);
        }

        public static MessageBoxResult Show(string message, string title = "Notification")
        {
            return Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            _result = (YesBtn.Content.ToString() == "Yes") ? MessageBoxResult.Yes : MessageBoxResult.OK;
            this.Close();
        }

        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            _result = MessageBoxResult.No;
            this.Close();
        }

        // Smooth scale + fade entrance so confirmations don't just snap onto the screen.
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.18)) { EasingFunction = ease });

            if (RootBorder.RenderTransform is ScaleTransform scale)
            {
                var grow = new DoubleAnimation(0.92, 1, TimeSpan.FromSeconds(0.22))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }
    }
}
