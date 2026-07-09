using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Wisp.Plugins;
using Wisp.Plugins.Settings;

namespace Wisp
{
    /// <summary>
    /// The auto-generated settings dialog for a plugin's declarative <see cref="PluginSettingField"/>s.
    /// Controls are built in code from the field list and styled entirely off the app palette (via
    /// DynamicResource), so the dialog matches Wisp and follows the user's accent/theme live.
    /// </summary>
    public partial class PluginSettingsDialog : Window
    {
        private readonly IReadOnlyList<PluginSettingField> _fields;
        private readonly Dictionary<string, Func<object>> _valueGetters = new();

        public IReadOnlyDictionary<string, object>? ResultValues { get; private set; }

        public PluginSettingsDialog(string pluginName, IReadOnlyList<PluginSettingField> fields)
        {
            InitializeComponent();
            TitleText.Text = $"{pluginName.ToUpperInvariant()} SETTINGS";
            _fields = fields ?? Array.Empty<PluginSettingField>();

            GenerateUi();
        }

        private void GenerateUi()
        {
            foreach (var field in _fields)
            {
                var container = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };

                if (field is BoolSettingField boolField)
                {
                    // Label + toggle share a row, the way the app's own settings toggles read.
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var lbl = MakeLabel(field.Label);
                    lbl.VerticalAlignment = VerticalAlignment.Center;
                    Grid.SetColumn(lbl, 0);
                    row.Children.Add(lbl);

                    var toggle = new CheckBox
                    {
                        IsChecked = boolField.CurrentValue,
                        Style = (Style)FindResource("PluginToggle")
                    };
                    Grid.SetColumn(toggle, 1);
                    row.Children.Add(toggle);

                    container.Children.Add(row);
                    _valueGetters[field.Key] = () => toggle.IsChecked == true;
                }
                else
                {
                    container.Children.Add(MakeLabel(field.Label));

                    if (field is StringSettingField stringField)
                    {
                        var tb = new TextBox
                        {
                            Text = stringField.CurrentValue,
                            Style = (Style)FindResource("PluginTextBox"),
                            Margin = new Thickness(0, 6, 0, 0)
                        };
                        container.Children.Add(tb);
                        _valueGetters[field.Key] = () => tb.Text;
                    }
                    else if (field is NumberSettingField numField)
                    {
                        // A right-aligned live value readout above an accent slider.
                        var header = new Grid { Margin = new Thickness(0, 6, 0, 2) };
                        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var valueText = new TextBlock
                        {
                            Text = FormatNumber(numField.CurrentValue),
                            FontSize = 12,
                            HorizontalAlignment = HorizontalAlignment.Right
                        };
                        valueText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                        valueText.SetResourceReference(TextBlock.FontFamilyProperty, "FontMono");
                        Grid.SetColumn(valueText, 1);
                        header.Children.Add(valueText);
                        container.Children.Add(header);

                        var slider = new Slider
                        {
                            Minimum = numField.Min,
                            Maximum = numField.Max,
                            Value = Clamp(numField.CurrentValue, numField.Min, numField.Max),
                            Style = (Style)FindResource("PluginSlider")
                        };
                        if (numField.Step > 0)
                        {
                            slider.TickFrequency = numField.Step;
                            slider.IsSnapToTickEnabled = true;
                        }
                        slider.ValueChanged += (s, e) => valueText.Text = FormatNumber(slider.Value);
                        container.Children.Add(slider);
                        _valueGetters[field.Key] = () => slider.Value;
                    }
                    else if (field is ChoiceSettingField choiceField)
                    {
                        var combo = new ComboBox
                        {
                            ItemsSource = choiceField.Choices,
                            SelectedItem = choiceField.CurrentValue,
                            Style = (Style)FindResource("PluginCombo"),
                            Margin = new Thickness(0, 6, 0, 0)
                        };
                        container.Children.Add(combo);
                        _valueGetters[field.Key] = () => combo.SelectedItem?.ToString() ?? choiceField.CurrentValue;
                    }
                }

                if (!string.IsNullOrWhiteSpace(field.Description))
                    container.Children.Add(MakeDescription(field.Description!));

                SettingsPanel.Children.Add(container);
            }
        }

        private static TextBlock MakeLabel(string text)
        {
            var tb = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            return tb;
        }

        private static TextBlock MakeDescription(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            return tb;
        }

        private static string FormatNumber(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
            var scaleInX = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } };
            var scaleInY = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(250)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } };

            BeginAnimation(OpacityProperty, fadeIn);
            var transform = (System.Windows.Media.ScaleTransform)RootBorder.RenderTransform;
            transform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleInX);
            transform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleInY);
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            ResultValues = null;
            CloseAnimated();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var results = new Dictionary<string, object>();
            foreach (var kvp in _valueGetters)
                results[kvp.Key] = kvp.Value();
            ResultValues = results;
            CloseAnimated();
        }

        private void CloseAnimated()
        {
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
            fadeOut.Completed += (s, e) => Close();
            BeginAnimation(OpacityProperty, fadeOut);
        }
    }
}
