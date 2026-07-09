using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Wisp.Models;
using Wisp.Services;

namespace Wisp
{
    public partial class OnboardingWindow : Window
    {
        private readonly AppSettings _settings;
        private int _currentPage = 0;
        private bool _isRebinding = false;
        private List<int> _reboundKeys = new();
        private string _hotkeyText = "F9";
        
        private string _selectedThemePreset = "Wisp Cyan";
        private string _selectedAccentHex = "";

        public OnboardingWindow(AppSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            
            // Load initial state from settings
            _selectedThemePreset = settings.ThemePreset;
            _selectedAccentHex = settings.AccentColorHex;
            _hotkeyText = settings.HotkeyText;
            _reboundKeys = settings.GetHotkeyKeysList();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Populate Theme Preset Swatches
            PopulatePresetSwatches();
            
            // Set Hotkey button initial text
            HotkeyBtn.Content = _hotkeyText;

            // Set Output folder text box
            OutputFolderTextBox.Text = _settings.OutputFolder;

            // Set Audio toggles
            SysAudioCheck.IsChecked = _settings.SystemAudioEnabled;
            MicCheck.IsChecked = _settings.MicrophoneEnabled;
            SocialAudioCheck.IsChecked = _settings.SocialAudioEnabled;

            // Populate Microphone device list
            PopulateMicrophoneDevices();
            UpdateMicPanelVisibility();

            // Set up initial theme preview ring
            UpdatePresetHighlights();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { this.DragMove(); } catch { }
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = CustomMessageBox.Show(
                "Are you sure you want to exit the setup wizard? Wisp requires configuration to run.",
                "Exit Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                if (_isRebinding)
                {
                    var hookManager = ((App)Application.Current).HookManager;
                    hookManager.HotkeyReboundLive -= HookManager_HotkeyReboundLive;
                    hookManager.HotkeyRebound -= HookManager_HotkeyRebound;
                    hookManager.StopRebindingMode();
                }
                this.DialogResult = false;
                this.Close();
            }
        }

        // ================= PAGE TRANSITIONS =================
        private void NextBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage < 5)
            {
                SwitchPage(_currentPage + 1);
            }
        }

        private void BackBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 0)
            {
                SwitchPage(_currentPage - 1);
            }
        }

        private void SwitchPage(int targetPage)
        {
            if (targetPage < 0 || targetPage > 5) return;

            var currentPageBorder = GetPageBorder(_currentPage);
            var nextPageBorder = GetPageBorder(targetPage);

            if (currentPageBorder == null || nextPageBorder == null) return;

            // Animate transition: fade out current, fade in next
            var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (s, ev) =>
            {
                currentPageBorder.Visibility = Visibility.Collapsed;
                nextPageBorder.Visibility = Visibility.Visible;
                nextPageBorder.Opacity = 0;

                var fadeIn = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200));
                nextPageBorder.BeginAnimation(OpacityProperty, fadeIn);
                
                _currentPage = targetPage;
                UpdateNavigationButtons();
                UpdateSummaryTexts();
            };
            currentPageBorder.BeginAnimation(OpacityProperty, fadeOut);
        }

        private Border? GetPageBorder(int pageIndex)
        {
            return pageIndex switch
            {
                0 => Page0,
                1 => Page1,
                2 => Page2,
                3 => Page3,
                4 => Page4,
                5 => Page5,
                _ => null
            };
        }

        private void UpdateNavigationButtons()
        {
            // Back button visible everywhere except page 0
            BackBtn.Visibility = _currentPage > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Next button visible on pages 1 to 4
            NextBtn.Visibility = (_currentPage > 0 && _currentPage < 5) ? Visibility.Visible : Visibility.Collapsed;

            // Update dot indicators
            var accentColor = ThemeManager.AccentColor;
            var borderBrush = (SolidColorBrush)FindResource("PanelBorderBrush");
            var accentBrush = (SolidColorBrush)FindResource("AccentBrush");

            for (int i = 0; i <= 5; i++)
            {
                var dot = FindName($"Dot{i}") as System.Windows.Shapes.Ellipse;
                if (dot != null)
                {
                    dot.Fill = (i == _currentPage) ? accentBrush : borderBrush;
                }
            }
        }

        private void UpdateSummaryTexts()
        {
            if (_currentPage == 5)
            {
                SummaryThemeText.Text = string.IsNullOrEmpty(_selectedAccentHex) ? _selectedThemePreset : $"Custom Color ({_selectedAccentHex})";
                SummaryHotkeyText.Text = _hotkeyText;
                SummaryFolderText.Text = OutputFolderTextBox.Text;

                var audioModes = new List<string>();
                if (SysAudioCheck.IsChecked == true) audioModes.Add("System Audio");
                if (MicCheck.IsChecked == true) audioModes.Add("Microphone");
                if (SocialAudioCheck.IsChecked == true) audioModes.Add("Social Isolation");
                SummaryAudioText.Text = audioModes.Count > 0 ? string.Join(", ", audioModes) : "Muted (None)";
            }
        }

        // ================= STEP 1: PERSONALIZATION (THEME) =================
        private void PopulatePresetSwatches()
        {
            if (PresetSwatchPanel == null || PresetSwatchPanel.Children.Count > 0) return;
            foreach (var (name, hex) in ThemeManager.Presets)
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var inner = new Border
                {
                    Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
                    Background = new SolidColorBrush(color),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var outer = new Border
                {
                    Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
                    BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                    Margin = new Thickness(0, 0, 15, 6), Cursor = Cursors.Hand,
                    Tag = name, ToolTip = name, Child = inner
                };
                outer.MouseLeftButtonUp += PresetSwatch_Click;
                PresetSwatchPanel.Children.Add(outer);
            }
        }

        private void PresetSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string name)
            {
                string hex = ThemeManager.Presets.First(p => p.Name == name).Hex;
                _selectedThemePreset = name;
                _selectedAccentHex = ""; // clear custom
                ThemeManager.ApplyAccentHex(hex);
                UpdatePresetHighlights();
            }
        }

        private void UpdatePresetHighlights()
        {
            if (PresetSwatchPanel == null) return;
            var accentBrush = (SolidColorBrush)FindResource("AccentBrush");
            foreach (var child in PresetSwatchPanel.Children)
            {
                if (child is Border ob)
                {
                    ob.BorderBrush = (ob.Tag as string == _selectedThemePreset) ? accentBrush : Brushes.Transparent;
                }
            }
        }

        // ================= STEP 2: HOTKEY REBINDING =================
        private void HotkeyBtn_Click(object sender, RoutedEventArgs e)
        {
            StartHotkeyRebinding();
        }

        private void StartHotkeyRebinding()
        {
            if (_isRebinding) return;
            _isRebinding = true;

            HotkeyBtn.Content = "[ Press keys... ]";
            HotkeyBtn.Foreground = Brushes.Tomato;

            var hookManager = ((App)Application.Current).HookManager;
            hookManager.StartRebindingMode();
            hookManager.HotkeyReboundLive += HookManager_HotkeyReboundLive;
            hookManager.HotkeyRebound += HookManager_HotkeyRebound;
        }

        private void HookManager_HotkeyReboundLive(string displayText)
        {
            Dispatcher.Invoke(() =>
            {
                HotkeyBtn.Content = displayText;
            });
        }

        private void HookManager_HotkeyRebound(List<int> keys, string text)
        {
            var hookManager = ((App)Application.Current).HookManager;
            hookManager.HotkeyReboundLive -= HookManager_HotkeyReboundLive;
            hookManager.HotkeyRebound -= HookManager_HotkeyRebound;
            hookManager.StopRebindingMode();

            Dispatcher.Invoke(() =>
            {
                _isRebinding = false;
                _reboundKeys = keys;
                _hotkeyText = text;
                HotkeyBtn.Content = text;
                
                var accentBrush = (SolidColorBrush)FindResource("AccentBrush");
                HotkeyBtn.Foreground = accentBrush;
            });
        }

        // ================= STEP 3: OUTPUT FOLDER =================
        private void BrowseBtn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Output Folder for Wisp Captures",
                InitialDirectory = Directory.Exists(OutputFolderTextBox.Text) ? OutputFolderTextBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };

            if (dialog.ShowDialog() == true)
            {
                OutputFolderTextBox.Text = dialog.FolderName;
            }
        }

        // ================= STEP 4: AUDIO CAPTURE =================
        private void MicCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateMicPanelVisibility();
        }

        private void UpdateMicPanelVisibility()
        {
            if (MicDevicePanel != null)
                MicDevicePanel.Visibility = MicCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PopulateMicrophoneDevices()
        {
            MicDeviceCombo.Items.Clear();
            var app = (App)Application.Current;
            var mics = app.RecorderService.GetMicrophoneDevices();
            
            foreach (var mic in mics)
            {
                MicDeviceCombo.Items.Add(mic);
            }

            // Attempt to match selected
            if (!string.IsNullOrEmpty(_settings.MicrophoneDevice) && MicDeviceCombo.Items.Contains(_settings.MicrophoneDevice))
            {
                MicDeviceCombo.SelectedItem = _settings.MicrophoneDevice;
            }
            else if (MicDeviceCombo.Items.Count > 0)
            {
                MicDeviceCombo.SelectedIndex = 0;
            }
        }

        // ================= STEP 5: FINISH =================
        private void FinishBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isRebinding)
            {
                var hookManager = ((App)Application.Current).HookManager;
                hookManager.HotkeyReboundLive -= HookManager_HotkeyReboundLive;
                hookManager.HotkeyRebound -= HookManager_HotkeyRebound;
                hookManager.StopRebindingMode();
            }

            // Apply and Save settings
            _settings.ThemePreset = _selectedThemePreset;
            _settings.AccentColorHex = _selectedAccentHex;
            _settings.OutputFolder = OutputFolderTextBox.Text.Trim();
            
            _settings.SetHotkeyKeysList(_reboundKeys, _hotkeyText);
            
            _settings.SystemAudioEnabled = SysAudioCheck.IsChecked == true;
            _settings.MicrophoneEnabled = MicCheck.IsChecked == true;
            _settings.MicrophoneDevice = MicDeviceCombo.SelectedItem?.ToString() ?? "";
            _settings.SocialAudioEnabled = SocialAudioCheck.IsChecked == true;
            _settings.HasCompletedOnboarding = true;

            _settings.Save();

            // Re-apply theme permanently
            ThemeManager.Apply(_settings);

            this.DialogResult = true;
            this.Close();
        }
    }
}
