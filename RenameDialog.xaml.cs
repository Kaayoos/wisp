using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Wisp
{
    public partial class RenameDialog : Window
    {
        public string ResultText { get; private set; } = "";

        public RenameDialog(string defaultText, string title = "Rename Clip")
        {
            InitializeComponent();
            DialogTitle.Text = title;
            Title = title;
            InputTextBox.Text = defaultText;
            InputTextBox.Focus();
        }

        // Smooth scale + fade entrance, matching the rest of the app's dialogs.
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

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmSelection();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void InputTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            InputTextBox.SelectAll();
        }

        private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ConfirmSelection();
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }

        private void ConfirmSelection()
        {
            ResultText = InputTextBox.Text.Trim();
            if (!string.IsNullOrEmpty(ResultText))
            {
                DialogResult = true;
                Close();
            }
        }
    }
}
