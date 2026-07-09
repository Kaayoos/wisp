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
        // ================= INTEGRATED SETTINGS LOGIC =================
        private void LoadCurrentSettings()
        {
            var settings = _app.Settings;

            // Appearance - reflect the saved accent and re-apply it.
            PopulatePresetSwatches();
            _pendingThemePreset = settings.ThemePreset;
            _pendingAccentHex = settings.AccentColorHex;
            ThemeManager.Apply(settings);
            UpdateAppearanceVisuals(ThemeManager.ResolveAccentHex(settings),
                string.IsNullOrWhiteSpace(settings.AccentColorHex) ? settings.ThemePreset : null,
                updateHexBox: true, updateHue: true);
            _pendingActiveThemeId = settings.ActiveThemeId ?? "";
            PopulateThemeChips(); // rebuilds each open so plugin-registered themes show up
            TargetCursorCheck.IsChecked = settings.TargetCursorEnabled;

            // Notifications & Output
            PopulateMonitorList(settings.NotificationMonitorIndex);
            _pendingNotifPosPctX = settings.NotificationPosPctX;
            _pendingNotifPosPctY = settings.NotificationPosPctY;
            FilenameTemplateBox.Text = settings.FilenameTemplate;
            ShowProgressCheck.IsChecked = settings.ShowCaptureProgress;

            // Folder
            OutputFolderTextBox.Text = settings.OutputFolder;

            // Buffer length
            BufferSlider.Value = settings.BufferLengthSeconds;
            BufferValueText.Text = FormatBufferLabel(settings.BufferLengthSeconds);

            // Clip chaining. The window can't exceed the buffer length (so consecutive taps always
            // overlap), nor a 1-minute cap - so the slider max tracks the buffer.
            ChainingCheck.IsChecked = settings.ClipChainingEnabled;
            UpdateChainWindowRange();
            ChainWindowSlider.Value = Math.Min(settings.ChainWindowSeconds, ChainWindowSlider.Maximum);
            ChainWindowValueText.Text = $"{(int)ChainWindowSlider.Value}s";
            UpdateChainingPanelVisibility();

            // Hotkey
            _reboundKeys = settings.GetHotkeyKeysList();
            _hotkeyText = settings.HotkeyText;
            HotkeyBtn.Content = _hotkeyText;

            // Audio
            SysAudioCheck.IsChecked = settings.SystemAudioEnabled;
            MicCheck.IsChecked = settings.MicrophoneEnabled;
            
            // Populate Mic Devices
            PopulateMicrophoneDevices(settings.MicrophoneDevice);
            UpdateMicPanelVisibility();

            // Audio mix levels + sync offset
            SysVolumeSlider.Value = settings.SystemAudioVolume;
            SysVolumeValueText.Text = $"{settings.SystemAudioVolume}%";
            MicVolumeSlider.Value = settings.MicrophoneVolume;
            MicVolumeValueText.Text = $"{settings.MicrophoneVolume}%";
            SocialAudioCheck.IsChecked = settings.SocialAudioEnabled;
            SocialVolumeSlider.Value = settings.SocialAudioVolume;
            SocialVolumeValueText.Text = $"{settings.SocialAudioVolume}%";
            SocialAppsTextBox.Text = settings.GetSocialAppsText();
            UpdateSocialPanelVisibility();
            AudioOffsetSlider.Value = settings.AudioOffsetMs;
            AudioOffsetValueText.Text = settings.AudioOffsetMs > 0 ? $"+{settings.AudioOffsetMs} ms" : $"{settings.AudioOffsetMs} ms";

            // Voice trigger
            VoiceTriggerCheck.IsChecked = settings.VoiceTriggerEnabled;
            string langToSelect = string.IsNullOrEmpty(settings.VoiceTriggerLanguage) ? "en" : settings.VoiceTriggerLanguage.ToLowerInvariant();
            var itemToSelect = VoiceLangCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == langToSelect);
            if (itemToSelect != null) VoiceLangCombo.SelectedItem = itemToSelect;
            else VoiceLangCombo.SelectedIndex = 0;
            VoicePhraseBox.Text = settings.VoiceTriggerPhrase; // set after combo so the saved phrase wins
            UpdateVoicePanelVisibility();

            // Video Preset
            QualityCombo.SelectedIndex = settings.VideoQuality switch
            {
                "Low" => 0,
                "High" => 2,
                _ => 1 // Medium
            };

            // Audio Preset (AAC bitrate)
            AudioQualityCombo.SelectedIndex = settings.AudioQuality switch
            {
                "High" => 1,
                "Studio" => 2,
                _ => 0 // Standard
            };

            // FPS (non-editable dropdown - select the saved value)
            PopulateFpsPresets();
            SelectComboValue(FpsCombo, settings.CaptureFps.ToString());

            // Resolution (non-editable dropdown - select the saved value)
            PopulateResolutionPresets();
            SelectComboValue(ResolutionCombo, ResolutionToDisplay(settings.CaptureResolution));

            // Recording GPU / encoder
            PopulateGpuList(settings.RecordingGpu);

            // Advanced encoding: codec + optional custom bitrate
            VideoCodecCombo.SelectedIndex = settings.VideoCodec switch
            {
                "H.265" => 1,
                "AV1" => 2,
                _ => 0 // H.264
            };
            UpdateVideoCodecHint();
            CustomBitrateCheck.IsChecked = settings.CustomBitrateEnabled;
            CustomBitrateSlider.Value = Math.Clamp(settings.CustomVideoBitrateMbps, (int)CustomBitrateSlider.Minimum, (int)CustomBitrateSlider.Maximum);
            CustomBitrateValueText.Text = $"{(int)CustomBitrateSlider.Value} Mbps";
            UpdateCustomBitratePanelVisibility();

            // Which monitor to record (Auto = follow the active game)
            PopulateRecordMonitorList(settings.RecordMonitor);

            // Startup Check
            StartupCheck.IsChecked = settings.LaunchOnStartup;

            // Recording mode (auto-detect / always-on / manual) + auto-detect options
            RecordingModeCombo.SelectedIndex = settings.RecordingMode switch
            {
                "AlwaysOn" => 1,
                "Manual" => 2,
                _ => 0 // Auto
            };
            ImportGamesCheck.IsChecked = settings.ImportInstalledGames;
            AutoRecordNotifyCheck.IsChecked = settings.AutoRecordNotify;
            AlwaysRecordTextBox.Text = settings.GetAlwaysRecordText();
            NeverRecordTextBox.Text = settings.GetNeverRecordText();
            UpdateAutoDetectPanelVisibility();

            // Capture Sound
            if (CaptureSoundCheck != null)
            {
                CaptureSoundCheck.IsChecked = settings.CaptureSoundEnabled;
                CaptureSoundVolumeSlider.Value = settings.CaptureSoundVolume;
                CaptureSoundVolumeText.Text = $"{settings.CaptureSoundVolume}%";
                UpdateCaptureSoundPanelVisibility();
            }

            // Kill detection
            KillDetectionCheck.IsChecked = settings.KillDetectionEnabled;
            KillLolCheck.IsChecked = settings.KillDetectLol;
            KillCs2Check.IsChecked = settings.KillDetectCs2;
            KillValorantCheck.IsChecked = settings.KillDetectValorant;
            KillOverwatchCheck.IsChecked = settings.KillDetectOverwatch;
            KillMarkersCheck.IsChecked = settings.KillMarkersEnabled;
            KillAutoClipCheck.IsChecked = settings.KillAutoClipEnabled;
            KillAutoClipDelaySlider.Value = Math.Clamp(settings.KillAutoClipDelaySeconds, (int)KillAutoClipDelaySlider.Minimum, (int)KillAutoClipDelaySlider.Maximum);
            KillAutoClipDelayValueText.Text = $"{(int)KillAutoClipDelaySlider.Value}s";
            KillAutoClipCooldownSlider.Value = Math.Clamp(settings.KillAutoClipCooldownSeconds, (int)KillAutoClipCooldownSlider.Minimum, (int)KillAutoClipCooldownSlider.Maximum);
            KillAutoClipCooldownValueText.Text = $"{(int)KillAutoClipCooldownSlider.Value}s";
            Cs2GsiPortBox.Text = settings.Cs2GsiPort.ToString();
            KillDiagCheck.IsChecked = settings.KillDetectionDiagnostics;
            UpdateKillDetectionPanelVisibility();
            RefreshCs2GsiStatus();
        }

        private void PopulateMonitorList(int selectedIndex)
        {
            if (NotifMonitorCombo == null) return;
            NotifMonitorCombo.Items.Clear();
            var screens = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                NotifMonitorCombo.Items.Add($"Monitor {i + 1}" + (screens[i].Primary ? " (Primary)" : ""));
            }
            if (selectedIndex >= 0 && selectedIndex < NotifMonitorCombo.Items.Count)
                NotifMonitorCombo.SelectedIndex = selectedIndex;
            else if (NotifMonitorCombo.Items.Count > 0)
                NotifMonitorCombo.SelectedIndex = 0;
        }

        // The "record this monitor" picker: an "Auto" row (follow the active game) followed by one row per
        // display. Selection is persisted as "Auto" or the screen's stable device name (survives index
        // reshuffles better than a bare ordinal).
        private void PopulateRecordMonitorList(string? selected)
        {
            if (RecordMonitorCombo == null) return;
            RecordMonitorCombo.Items.Clear();
            RecordMonitorCombo.Items.Add("Auto (follow active game)");
            var screens = System.Windows.Forms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
                RecordMonitorCombo.Items.Add($"Monitor {i + 1}" + (screens[i].Primary ? " (Primary)" : ""));

            if (string.IsNullOrWhiteSpace(selected) || selected.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                RecordMonitorCombo.SelectedIndex = 0;
            }
            else
            {
                int screenIdx = Array.FindIndex(screens, s => string.Equals(s.DeviceName, selected, StringComparison.OrdinalIgnoreCase));
                RecordMonitorCombo.SelectedIndex = screenIdx >= 0 ? screenIdx + 1 : 0; // +1 for the Auto row
            }
        }

        /// <summary>Maps the record-monitor combo selection to a setting value: "Auto" or a device name.</summary>
        private string RecordMonitorComboToSetting()
        {
            int sel = RecordMonitorCombo?.SelectedIndex ?? 0;
            if (sel <= 0) return "Auto"; // row 0 is Auto
            var screens = System.Windows.Forms.Screen.AllScreens;
            int screenIdx = sel - 1;
            return (screenIdx >= 0 && screenIdx < screens.Length) ? screens[screenIdx].DeviceName : "Auto";
        }

        private void PopulateMicrophoneDevices(string selectedDevice)
        {
            MicDeviceCombo.Items.Clear();
            var mics = _app.RecorderService.GetMicrophoneDevices();
            
            foreach (var mic in mics)
            {
                MicDeviceCombo.Items.Add(mic);
            }

            if (!string.IsNullOrEmpty(selectedDevice) && MicDeviceCombo.Items.Contains(selectedDevice))
            {
                MicDeviceCombo.SelectedItem = selectedDevice;
            }
            else if (MicDeviceCombo.Items.Count > 0)
            {
                MicDeviceCombo.SelectedIndex = 0;
            }
        }

        private void PopulateGpuList(string selected)
        {
            GpuCombo.Items.Clear();
            GpuCombo.Items.Add("Auto");
            foreach (var gpu in _app.RecorderService.GetAvailableGpus())
                GpuCombo.Items.Add(gpu);
            GpuCombo.Items.Add("CPU (no GPU)");

            if (!string.IsNullOrEmpty(selected) && GpuCombo.Items.Contains(selected))
                GpuCombo.SelectedItem = selected;
            else
                GpuCombo.SelectedIndex = 0; // Auto
        }

        private void PopulateFpsPresets()
        {
            if (FpsCombo.Items.Count > 0) return;
            foreach (var fps in new[] { "30", "48", "60", "75", "90", "120", "144", "165", "240" })
                FpsCombo.Items.Add(fps);
        }

        private void PopulateResolutionPresets()
        {
            if (ResolutionCombo.Items.Count > 0) return;
            foreach (var r in new[] { "Native", "2160p", "1440p", "1080p", "900p", "720p", "480p" })
                ResolutionCombo.Items.Add(r);
        }

        private static string ResolutionToDisplay(string stored)
            => string.IsNullOrWhiteSpace(stored) ? "Native" : stored.Trim();

        /// <summary>Selects the item matching <paramref name="value"/> in a (now non-editable) combo,
        /// adding it first if the saved value isn't one of the presets so a prior custom choice isn't lost.</summary>
        private static void SelectComboValue(ComboBox combo, string value)
        {
            foreach (var item in combo.Items)
            {
                if (string.Equals(item?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            combo.SelectedIndex = combo.Items.Add(value);
        }

        /// <summary>Human-readable buffer length: "45s" under a minute, otherwise "2m" / "2m 30s".</summary>
        private static string FormatBufferLabel(int seconds)
        {
            if (seconds < 60) return $"{seconds}s";
            int m = seconds / 60, s = seconds % 60;
            return s == 0 ? $"{m}m" : $"{m}m {s}s";
        }

        private static int ParseFpsInput(string? text, int fallback)
        {
            string digits = new string((text ?? "").Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out int fps) && fps > 0)
                return Math.Clamp(fps, 5, 480);
            return fallback;
        }

        private static string NormalizeResolutionInput(string? text)
        {
            string r = (text ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(r) || r.StartsWith("native")) return "Native";

            int xi = r.IndexOf('x');
            if (xi < 0) xi = r.IndexOf('×');
            if (xi > 0)
            {
                string wp = new string(r.Substring(0, xi).Where(char.IsDigit).ToArray());
                string hp = new string(r.Substring(xi + 1).Where(char.IsDigit).ToArray());
                if (int.TryParse(wp, out int w) && int.TryParse(hp, out int h) && w > 0 && h > 0)
                    return $"{w}x{h}";
                return "Native";
            }

            string heightDigits = new string(r.Where(char.IsDigit).ToArray());
            if (int.TryParse(heightDigits, out int height) && height > 0)
                return $"{height}p";

            return "Native";
        }

        // ================= APPEARANCE / THEMING =================
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
                    Margin = new Thickness(0, 0, 10, 6), Cursor = Cursors.Hand,
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
                _pendingThemePreset = name;
                _pendingAccentHex = ""; // a preset clears any custom color
                ThemeManager.ApplyAccentHex(hex); // live preview
                UpdateAppearanceVisuals(hex, name, updateHexBox: true, updateHue: true);
                _pendingActiveThemeId = ""; // choosing an accent reverts to the default palette
                HighlightThemeChips();
            }
        }

        // ── Full themes (built-in + plugin), shown as selectable chips above the accent picker ──
        private void PopulateThemeChips()
        {
            if (ThemePanel == null) return;
            ThemePanel.Children.Clear();

            // "Default" (accent-only) first, then every registered full theme (built-in + plugin).
            AddThemeChip(ThemeManager.DefaultThemeId, "Default");
            foreach (var t in ThemeManager.GetThemes())
            {
                if (string.Equals(t.Id, ThemeManager.DefaultThemeId, StringComparison.OrdinalIgnoreCase)) continue;
                AddThemeChip(t.Id, t.Name);
            }
            HighlightThemeChips();
        }

        private void AddThemeChip(string id, string name)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Cursor = Cursors.Hand,
                Tag = id,
                Child = new TextBlock
                {
                    Text = name,
                    Foreground = (Brush)FindResource("TextPrimaryBrush"),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold
                }
            };
            chip.MouseLeftButtonUp += ThemeChip_Click;
            ThemePanel.Children.Add(chip);
        }

        private void ThemeChip_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border b || b.Tag is not string id) return;

            if (string.Equals(id, ThemeManager.DefaultThemeId, StringComparison.OrdinalIgnoreCase))
            {
                _pendingActiveThemeId = "";
                ThemeManager.ApplyAccentHex(ResolvePendingAccentHex()); // default palette + the pending accent
            }
            else
            {
                _pendingActiveThemeId = id;
                ThemeManager.ApplyThemeById(id); // live preview (the theme brings its own accent)
            }
            HighlightThemeChips();
        }

        private void HighlightThemeChips()
        {
            if (ThemePanel == null) return;
            string selected = string.IsNullOrEmpty(_pendingActiveThemeId) ? ThemeManager.DefaultThemeId : _pendingActiveThemeId;
            foreach (var child in ThemePanel.Children)
            {
                if (child is not Border b) continue;
                bool active = string.Equals(b.Tag as string, selected, StringComparison.OrdinalIgnoreCase);
                b.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "PanelBorderBrush");
                b.Background = (Brush)FindResource(active ? "AccentDimBrush" : "AppWellBrush");
            }
        }

        private string ResolvePendingAccentHex()
        {
            if (!string.IsNullOrWhiteSpace(_pendingAccentHex)) return _pendingAccentHex;
            foreach (var (name, hex) in ThemeManager.Presets)
                if (string.Equals(name, _pendingThemePreset, StringComparison.OrdinalIgnoreCase)) return hex;
            return ThemeManager.DefaultAccentHex;
        }

        private void CustomHueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressAppearanceEvents) return;
            string hex = ToHex(HsvToColor(CustomHueSlider.Value, 0.85, 1.0));
            _pendingAccentHex = hex;
            _pendingThemePreset = ""; // custom color
            ThemeManager.ApplyAccentHex(hex);
            UpdateAppearanceVisuals(hex, null, updateHexBox: true, updateHue: false);
            _pendingActiveThemeId = "";
            HighlightThemeChips();
        }

        private void CustomHexBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressAppearanceEvents) return;
            if (!TryParseHex(CustomHexBox.Text.Trim(), out Color color)) return;
            string hex = ToHex(color);
            _pendingAccentHex = hex;
            _pendingThemePreset = "";
            ThemeManager.ApplyAccentHex(hex);
            UpdateAppearanceVisuals(hex, null, updateHexBox: false, updateHue: true);
            _pendingActiveThemeId = "";
            HighlightThemeChips();
        }

        private void UpdateAppearanceVisuals(string hex, string? selectedPreset, bool updateHexBox, bool updateHue)
        {
            if (!TryParseHex(hex, out Color color)) return;
            _suppressAppearanceEvents = true;
            try
            {
                if (CustomPreviewSwatch != null) CustomPreviewSwatch.Background = new SolidColorBrush(color);
                if (updateHexBox && CustomHexBox != null) CustomHexBox.Text = hex;
                if (updateHue && CustomHueSlider != null) CustomHueSlider.Value = ColorToHue(color);
                if (PresetSwatchPanel != null)
                    foreach (var child in PresetSwatchPanel.Children)
                        if (child is Border ob)
                            ob.BorderBrush = (selectedPreset != null && (ob.Tag as string) == selectedPreset)
                                ? AppearanceRingBrush : Brushes.Transparent;
            }
            finally { _suppressAppearanceEvents = false; }
        }

        private static Color HsvToColor(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double r = 0, g = 0, bl = 0;
            if (h < 60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; bl = x; }
            else if (h < 240) { g = x; bl = c; }
            else if (h < 300) { r = x; bl = c; }
            else { r = c; bl = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((bl + m) * 255));
        }

        private static double ColorToHue(Color color)
        {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            if (d == 0) return 0;
            double h;
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
            return h < 0 ? h + 360 : h;
        }

        private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        private static bool TryParseHex(string text, out Color color)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(text) && text.StartsWith("#") && text.Length == 7)
                {
                    color = (Color)ColorConverter.ConvertFromString(text);
                    return true;
                }
            }
            catch { }
            color = Colors.Black;
            return false;
        }

        private void UpdateMicPanelVisibility()
        {
            MicDevicePanel.Visibility = (MicCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        }

        private void VoiceTriggerCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateVoicePanelVisibility();
        }

        private void UpdateVoicePanelVisibility()
        {
            if (VoicePhrasePanel != null)
                VoicePhrasePanel.Visibility = (VoiceTriggerCheck.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            
            UpdateVoiceModelUI();
        }

        private void UpdateVoiceModelUI()
        {
            if (VoiceTriggerCheck.IsChecked != true || VoiceLangCombo == null || VoicePhraseInputPanel == null || VoiceDownloadBtn == null) return;
            string lang = (VoiceLangCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en";
            
            if (VoiceTriggerManager.IsModelAvailable(lang))
            {
                VoiceDownloadBtn.Visibility = Visibility.Collapsed;
                VoicePhraseInputPanel.Visibility = Visibility.Visible;
            }
            else
            {
                VoiceDownloadBtn.Visibility = Visibility.Visible;
                VoicePhraseInputPanel.Visibility = Visibility.Collapsed;
            }
        }

        private async void VoiceDownloadBtn_Click(object sender, RoutedEventArgs e)
        {
            string lang = (VoiceLangCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en";
            if (VoiceTriggerManager.IsModelAvailable(lang)) return;

            VoiceDownloadBtn.Visibility = Visibility.Collapsed;
            VoiceDownloadPanel.Visibility = Visibility.Visible;
            VoiceTriggerCheck.IsEnabled = false;
            VoiceLangCombo.IsEnabled = false;

            try
            {
                var progress = new Progress<double>(p => VoiceDownloadProgress.Value = p * 100);
                await VoiceTriggerManager.DownloadModelAsync(lang, progress, System.Threading.CancellationToken.None);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Failed to download voice model: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                VoiceDownloadPanel.Visibility = Visibility.Collapsed;
                VoiceTriggerCheck.IsEnabled = true;
                VoiceLangCombo.IsEnabled = true;
                UpdateVoiceModelUI();
            }
        }

        private void VoiceLangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (VoicePhraseBox == null) return;
            string current = VoicePhraseBox.Text.Trim();
            bool isDefaultish = string.IsNullOrEmpty(current)
                || current.Equals("clip that", StringComparison.OrdinalIgnoreCase)
                || current.Equals("clip that for me", StringComparison.OrdinalIgnoreCase)
                || current.Equals("zapisz klip", StringComparison.OrdinalIgnoreCase);
            if (isDefaultish)
            {
                string t = (VoiceLangCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en";
                VoicePhraseBox.Text = t == "pl" ? "zapisz klip" : "clip that for me";
            }
            UpdateVoiceModelUI();
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Output Folder for Game Clips",
                InitialDirectory = Directory.Exists(OutputFolderTextBox.Text) ? OutputFolderTextBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
            };

            if (dialog.ShowDialog() == true)
            {
                OutputFolderTextBox.Text = dialog.FolderName;
            }
        }

        private void BufferSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BufferValueText != null)
            {
                BufferValueText.Text = FormatBufferLabel((int)BufferSlider.Value);
            }
            // The chain window can't outlast the buffer (taps must overlap), so re-cap it to match.
            UpdateChainWindowRange();
        }

        private void ChainingCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateChainingPanelVisibility();
        }

        private void UpdateChainingPanelVisibility()
        {
            if (ChainWindowPanel != null)
                ChainWindowPanel.Visibility = ChainingCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Caps the chain-window slider at the buffer length, up to a 1-minute maximum (e.g. a 30s buffer
        /// allows up to a 30s window; a 90s buffer allows the full 60s). Clamps the current value to fit.
        /// </summary>
        private void UpdateChainWindowRange()
        {
            if (ChainWindowSlider == null) return;
            int max = Math.Max((int)ChainWindowSlider.Minimum, Math.Min(60, (int)BufferSlider.Value));
            ChainWindowSlider.Maximum = max;
            if (ChainWindowMaxLabel != null) ChainWindowMaxLabel.Text = $"{max}s";
            if (ChainWindowSlider.Value > max) ChainWindowSlider.Value = max;
        }

        private void ChainWindowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (ChainWindowValueText != null)
                ChainWindowValueText.Text = $"{(int)ChainWindowSlider.Value}s";
        }

        // ===== Kill detection settings UI =====

        private void KillDetectionCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateKillDetectionPanelVisibility();
        }

        private void KillCs2Check_Changed(object sender, RoutedEventArgs e)
        {
            UpdateKillDetectionPanelVisibility();
            RefreshCs2GsiStatus();
        }

        private void KillAutoClipCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateKillDetectionPanelVisibility();
        }

        private void UpdateKillDetectionPanelVisibility()
        {
            if (KillDetectionPanel == null) return;
            KillDetectionPanel.Visibility = KillDetectionCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (KillCs2Panel != null)
                KillCs2Panel.Visibility = KillCs2Check.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            if (KillAutoClipPanel != null)
                KillAutoClipPanel.Visibility = KillAutoClipCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void KillAutoClipDelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (KillAutoClipDelayValueText != null)
                KillAutoClipDelayValueText.Text = $"{(int)KillAutoClipDelaySlider.Value}s";
        }

        private void KillAutoClipCooldownSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (KillAutoClipCooldownValueText != null)
                KillAutoClipCooldownValueText.Text = $"{(int)KillAutoClipCooldownSlider.Value}s";
        }

        private void LocateCs2Btn_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select CS2's csgo\\cfg folder (…\\Counter-Strike Global Offensive\\game\\csgo\\cfg)"
            };
            if (dialog.ShowDialog() == true)
            {
                _app.Settings.Cs2CfgDirOverride = dialog.FolderName;
                RefreshCs2GsiStatus();
            }
        }

        /// <summary>The GSI port the UI currently shows, falling back to the saved value when invalid.</summary>
        private int ParseCs2PortInput()
        {
            if (int.TryParse(Cs2GsiPortBox?.Text?.Trim(), out int port) && port >= 1024 && port <= 65535)
                return port;
            return _app.Settings.Cs2GsiPort;
        }

        /// <summary>Refreshes the "config installed / CS2 not found" status line under the CS2 toggle.</summary>
        private void RefreshCs2GsiStatus()
        {
            if (Cs2GsiStatusText == null) return;
            var settings = _app.Settings;
            if (Services.KillDetection.Cs2GsiInstaller.IsInstalled(ParseCs2PortInput(), settings.Cs2CfgDirOverride, out string path))
            {
                Cs2GsiStatusText.Text = $"Config installed: {path}";
            }
            else if (Services.KillDetection.Cs2GsiInstaller.FindCfgDir(settings.Cs2CfgDirOverride) != null)
            {
                Cs2GsiStatusText.Text = "CS2 found - the config installs when you save settings.";
            }
            else
            {
                Cs2GsiStatusText.Text = "CS2 not found automatically. Use \"Locate CS2 folder\" to pick the game's csgo\\cfg folder.";
            }
        }

        private void SysVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SysVolumeValueText != null)
                SysVolumeValueText.Text = $"{(int)SysVolumeSlider.Value}%";
        }

        private void MicVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MicVolumeValueText != null)
                MicVolumeValueText.Text = $"{(int)MicVolumeSlider.Value}%";
        }

        private void SocialVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SocialVolumeValueText != null)
                SocialVolumeValueText.Text = $"{(int)SocialVolumeSlider.Value}%";
        }

        private void SocialAudioCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateSocialPanelVisibility();
        }

        private void UpdateSocialPanelVisibility()
        {
            if (SocialOptionsPanel != null)
                SocialOptionsPanel.Visibility = SocialAudioCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RecordingModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateAutoDetectPanelVisibility();
        }

        /// <summary>The auto-detect options (game import, notify, always/never lists) only apply in Auto mode.</summary>
        private void UpdateAutoDetectPanelVisibility()
        {
            if (AutoDetectPanel != null)
                AutoDetectPanel.Visibility = RecordingModeCombo.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===== Advanced encoding (codec + custom bitrate) =====
        private void VideoCodecCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateVideoCodecHint();
        }

        /// <summary>Codec-specific guidance: H.264 is universal; H.265/AV1 trade compatibility for size.</summary>
        private void UpdateVideoCodecHint()
        {
            if (VideoCodecHint == null) return;
            VideoCodecHint.Text = VideoCodecCombo.SelectedIndex switch
            {
                1 => "Smaller files at the same quality, encoded on your GPU's HEVC encoder when available. " +
                     "H.265 clips need Windows' HEVC Video Extension to preview here, and some apps won't accept them.",
                2 => "The most efficient codec, but only the newest GPUs (RTX 40+, RX 7000+, Intel Arc) can encode it " +
                     "in real time - Wisp falls back automatically otherwise. AV1 clips need the AV1 Video Extension to preview here.",
                _ => "Plays everywhere and is accepted by every editor and platform. The safe default."
            };
        }

        private void CustomBitrateCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateCustomBitratePanelVisibility();
        }

        private void UpdateCustomBitratePanelVisibility()
        {
            if (CustomBitratePanel != null)
                CustomBitratePanel.Visibility = CustomBitrateCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CustomBitrateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (CustomBitrateValueText != null)
                CustomBitrateValueText.Text = $"{(int)CustomBitrateSlider.Value} Mbps";
        }

        private void AudioOffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (AudioOffsetValueText != null)
            {
                int ms = (int)AudioOffsetSlider.Value;
                AudioOffsetValueText.Text = ms > 0 ? $"+{ms} ms" : $"{ms} ms";
            }
        }

        private void MicCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateMicPanelVisibility();
        }

        private void HotkeyBtn_Click(object sender, RoutedEventArgs e)
        {
            StartHotkeyRebinding(false);
        }

        private void StartHotkeyRebinding(bool fromSidebar)
        {
            if (_isRebinding) return;

            _isRebinding = true;

            if (SettingsGrid.Visibility == Visibility.Visible)
            {
                HotkeyBtn.Content = "[ Press keys... ]";
                HotkeyBtn.Foreground = Brushes.Tomato;
                HotkeyBtn.BorderBrush = Brushes.Tomato;
            }
            HotkeyBadge.Text = "[ Recording... ]";
            HotkeyBadge.Foreground = Brushes.Tomato;

            _app.HookManager.StartRebindingMode();
            _app.HookManager.HotkeyReboundLive += HookManager_HotkeyReboundLive;
            _app.HookManager.HotkeyRebound += HookManager_HotkeyRebound;
        }

        private void HookManager_HotkeyReboundLive(string displayText)
        {
            Dispatcher.Invoke(() =>
            {
                if (SettingsGrid.Visibility == Visibility.Visible)
                {
                    HotkeyBtn.Content = displayText;
                }
                HotkeyBadge.Text = displayText;
            });
        }

        private void HookManager_HotkeyRebound(List<int> keys, string text)
        {
            _app.HookManager.HotkeyReboundLive -= HookManager_HotkeyReboundLive;
            _app.HookManager.HotkeyRebound -= HookManager_HotkeyRebound;
            _app.HookManager.StopRebindingMode();

            Dispatcher.Invoke(() =>
            {
                _isRebinding = false;

                if (SettingsGrid.Visibility == Visibility.Visible)
                {
                    _reboundKeys = keys;
                    _hotkeyText = text;
                    HotkeyBtn.Content = text;
                    HotkeyBtn.Foreground = ThemeManager.AccentBrush;
                    HotkeyBtn.BorderBrush = HotkeyBtn.Foreground;
                    
                    HotkeyBadge.Text = text;
                    HotkeyBadge.Foreground = ThemeManager.AccentBrush;
                }
                else
                {
                    var settings = _app.Settings;
                    settings.SetHotkeyKeysList(keys, text);
                    settings.Save();

                    _reboundKeys = keys;
                    _hotkeyText = text;
                    HotkeyBtn.Content = text;
                    HotkeyBadge.Text = text;
                    HotkeyBadge.Foreground = ThemeManager.AccentBrush;

                    _app.HookManager.UnregisterHotkey();
                    _app.HookManager.RegisterHotkey(settings.GetHotkeyKeysList());
                }
            });
        }

        private void SaveSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            var settings = _app.Settings;

            string outFolder = OutputFolderTextBox.Text.Trim();
            if (string.IsNullOrEmpty(outFolder))
            {
                CustomMessageBox.Show("Please select a valid output folder.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var savedKeys = settings.GetHotkeyKeysList();
            bool hotkeyChanged = _reboundKeys.Count != savedKeys.Count || !_reboundKeys.SequenceEqual(savedKeys);
            string previousOutputFolder = settings.OutputFolder;

            settings.OutputFolder = outFolder;
            
            // Notifications & Output
            settings.NotificationMonitorIndex = NotifMonitorCombo.SelectedIndex >= 0 ? NotifMonitorCombo.SelectedIndex : 0;
            settings.NotificationPosPctX = _pendingNotifPosPctX;
            settings.NotificationPosPctY = _pendingNotifPosPctY;
            settings.NotificationPosX = "Migrated";
            settings.NotificationPosY = "Migrated";
            string template = FilenameTemplateBox.Text.Trim();
            settings.FilenameTemplate = string.IsNullOrEmpty(template) ? "{Game}_{Date}_{Time}" : template;
            settings.ShowCaptureProgress = ShowProgressCheck.IsChecked == true;

            settings.BufferLengthSeconds = (int)BufferSlider.Value;
            settings.ClipChainingEnabled = ChainingCheck.IsChecked == true;
            settings.ChainWindowSeconds = (int)ChainWindowSlider.Value;
            settings.SetHotkeyKeysList(_reboundKeys, _hotkeyText);
            settings.SystemAudioEnabled = SysAudioCheck.IsChecked == true;
            settings.MicrophoneEnabled = MicCheck.IsChecked == true;
            settings.MicrophoneDevice = MicDeviceCombo.SelectedItem?.ToString() ?? "";
            settings.SystemAudioVolume = (int)SysVolumeSlider.Value;
            settings.MicrophoneVolume = (int)MicVolumeSlider.Value;
            settings.SocialAudioEnabled = SocialAudioCheck.IsChecked == true;
            settings.SocialAudioVolume = (int)SocialVolumeSlider.Value;
            settings.SetSocialAppsFromText(SocialAppsTextBox.Text);
            settings.AudioOffsetMs = (int)AudioOffsetSlider.Value;
            
            settings.VideoQuality = QualityCombo.SelectedIndex switch
            {
                0 => "Low",
                2 => "High",
                _ => "Medium"
            };

            settings.AudioQuality = AudioQualityCombo.SelectedIndex switch
            {
                1 => "High",
                2 => "Studio",
                _ => "Standard"
            };

            settings.CaptureFps = ParseFpsInput(FpsCombo.SelectedItem?.ToString(), settings.CaptureFps);
            settings.CaptureResolution = NormalizeResolutionInput(ResolutionCombo.SelectedItem?.ToString());
            settings.RecordingGpu = GpuCombo.SelectedItem?.ToString() ?? "Auto";

            settings.VideoCodec = VideoCodecCombo.SelectedIndex switch
            {
                1 => "H.265",
                2 => "AV1",
                _ => "H.264"
            };
            settings.CustomBitrateEnabled = CustomBitrateCheck.IsChecked == true;
            settings.CustomVideoBitrateMbps = (int)CustomBitrateSlider.Value;

            settings.RecordMonitor = RecordMonitorComboToSetting();
            settings.LaunchOnStartup = StartupCheck.IsChecked == true;

            // Recording mode + auto-detect options
            settings.RecordingMode = RecordingModeCombo.SelectedIndex switch
            {
                1 => "AlwaysOn",
                2 => "Manual",
                _ => "Auto"
            };
            settings.ImportInstalledGames = ImportGamesCheck.IsChecked == true;
            settings.AutoRecordNotify = AutoRecordNotifyCheck.IsChecked == true;
            settings.SetAlwaysRecordFromText(AlwaysRecordTextBox.Text);
            settings.SetNeverRecordFromText(NeverRecordTextBox.Text);

            // Capture Sound
            settings.CaptureSoundEnabled = CaptureSoundCheck.IsChecked == true;
            settings.CaptureSoundVolume = (int)CaptureSoundVolumeSlider.Value;

            // Kill detection
            settings.KillDetectionEnabled = KillDetectionCheck.IsChecked == true;
            settings.KillDetectLol = KillLolCheck.IsChecked == true;
            settings.KillDetectCs2 = KillCs2Check.IsChecked == true;
            settings.KillDetectValorant = KillValorantCheck.IsChecked == true;
            settings.KillDetectOverwatch = KillOverwatchCheck.IsChecked == true;
            settings.KillMarkersEnabled = KillMarkersCheck.IsChecked == true;
            settings.KillAutoClipEnabled = KillAutoClipCheck.IsChecked == true;
            settings.KillAutoClipDelaySeconds = (int)KillAutoClipDelaySlider.Value;
            settings.KillAutoClipCooldownSeconds = (int)KillAutoClipCooldownSlider.Value;
            settings.Cs2GsiPort = ParseCs2PortInput();

            settings.KillDetectionDiagnostics = KillDiagCheck.IsChecked == true;

            // CS2 detection needs its Game State Integration config in the game's cfg folder. Install
            // (or re-install after a port change) on save - the explicit user action that enables it.
            if (settings.KillDetectionEnabled && settings.KillDetectCs2 &&
                !Services.KillDetection.Cs2GsiInstaller.IsInstalled(settings.Cs2GsiPort, settings.Cs2CfgDirOverride, out _))
            {
                if (!Services.KillDetection.Cs2GsiInstaller.TryInstall(settings.Cs2GsiPort, settings.Cs2CfgDirOverride, out _, out string gsiError))
                    ShowToast("CS2 kill detection", gsiError, ToastKind.Warning);
            }
            RefreshCs2GsiStatus();

            // Appearance: persist accent + selected full theme
            settings.ThemePreset = _pendingThemePreset;
            settings.AccentColorHex = _pendingAccentHex;
            settings.ActiveThemeId = _pendingActiveThemeId ?? "";
            settings.TargetCursorEnabled = TargetCursorCheck.IsChecked == true;

            settings.VoiceTriggerEnabled = VoiceTriggerCheck.IsChecked == true;
            settings.VoiceTriggerLanguage = (VoiceLangCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en";
            string voicePhrase = VoicePhraseBox.Text.Trim();
            settings.VoiceTriggerPhrase = string.IsNullOrWhiteSpace(voicePhrase)
                ? (settings.VoiceTriggerLanguage == "pl" ? "zapisz klip" : "clip that for me")
                : voicePhrase;

            settings.Save();

            HotkeyBadge.Text = settings.HotkeyText;

            if (hotkeyChanged)
            {
                _app.HookManager.UnregisterHotkey();
                _app.HookManager.RegisterHotkey(settings.GetHotkeyKeysList());
            }

            SetStartupRegistry(settings.LaunchOnStartup);

            if (_app.RecorderService.IsRecording)
            {
                _app.StopBackgroundRecording();
                _app.StartBackgroundRecording();
            }

            if (!string.Equals(previousOutputFolder, outFolder, StringComparison.OrdinalIgnoreCase))
            {
                var recorder = _app.RecorderService;
                System.Threading.Tasks.Task.Run(() =>
                {
                    int imported = recorder.ImportUntrackedClips(outFolder);
                    if (imported > 0)
                        Dispatcher.BeginInvoke(new Action(LoadClips));
                });
            }

            _app.ApplyVoiceTriggerSettings();

            // Apply the recording mode last, so the detector/buffer reflect the freshly-saved settings.
            _app.ApplyRecordingModeSettings();

            // Start/stop kill detection to match the freshly-saved toggles - no restart needed.
            _app.ApplyKillDetectionSettings();

            if (settings.VoiceTriggerEnabled && !VoiceTriggerManager.IsModelAvailable(settings.VoiceTriggerLanguage))
            {
                CustomMessageBox.Show("Your settings were saved, but the speech model for the selected language is still downloading or failed to download. Voice-activated clipping will work once the model is available.",
                    "Voice Trigger", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                ShowToast("Settings saved", "Your configuration has been applied.", ToastKind.Success);
            }
        }

        private void CancelSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_isRebinding)
            {
                _app.HookManager.HotkeyReboundLive -= HookManager_HotkeyReboundLive;
                _app.HookManager.HotkeyRebound -= HookManager_HotkeyRebound;
                _app.HookManager.StopRebindingMode();
                _isRebinding = false;
            }

            LoadCurrentSettings();
            ShowToast("Changes discarded", null, ToastKind.Info);
        }

        private bool HasUnsavedSettingsChanges()
        {
            if (SettingsGrid.Visibility != Visibility.Visible) return false;

            var settings = _app.Settings;

            if (!string.Equals(_pendingThemePreset, settings.ThemePreset, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.Equals(_pendingAccentHex ?? "", settings.AccentColorHex ?? "", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.Equals(_pendingActiveThemeId ?? "", settings.ActiveThemeId ?? "", StringComparison.OrdinalIgnoreCase))
                return true;
            if (TargetCursorCheck.IsChecked != settings.TargetCursorEnabled)
                return true;

            if (!string.Equals(OutputFolderTextBox.Text.Trim(), settings.OutputFolder, StringComparison.OrdinalIgnoreCase))
                return true;

            // Notifications & Output
            if (NotifMonitorCombo.SelectedIndex >= 0 && NotifMonitorCombo.SelectedIndex != settings.NotificationMonitorIndex)
                return true;
            if (_pendingNotifPosPctX != settings.NotificationPosPctX || _pendingNotifPosPctY != settings.NotificationPosPctY)
                return true;
            string currentTemplate = FilenameTemplateBox.Text.Trim();
            if (string.IsNullOrEmpty(currentTemplate)) currentTemplate = "{Game}_{Date}_{Time}";
            if (currentTemplate != settings.FilenameTemplate)
                return true;
            if (ShowProgressCheck.IsChecked != settings.ShowCaptureProgress)
                return true;

            if ((int)BufferSlider.Value != settings.BufferLengthSeconds)
                return true;

            if (ChainingCheck.IsChecked != settings.ClipChainingEnabled)
                return true;
            if (ChainingCheck.IsChecked == true && (int)ChainWindowSlider.Value != settings.ChainWindowSeconds)
                return true;

            var currentKeys = _reboundKeys;
            var savedKeys = settings.GetHotkeyKeysList();
            if (currentKeys.Count != savedKeys.Count || !currentKeys.SequenceEqual(savedKeys))
                return true;

            if (SysAudioCheck.IsChecked != settings.SystemAudioEnabled)
                return true;
            if (MicCheck.IsChecked != settings.MicrophoneEnabled)
                return true;

            if (MicCheck.IsChecked == true)
            {
                string micDev = MicDeviceCombo.SelectedItem?.ToString() ?? "";
                if (!string.Equals(micDev, settings.MicrophoneDevice, StringComparison.OrdinalIgnoreCase))
                    return true;
                if ((int)MicVolumeSlider.Value != settings.MicrophoneVolume)
                    return true;
            }

            if ((int)SysVolumeSlider.Value != settings.SystemAudioVolume)
                return true;

            if (SocialAudioCheck.IsChecked != settings.SocialAudioEnabled)
                return true;
            if ((int)SocialVolumeSlider.Value != settings.SocialAudioVolume)
                return true;

            var tmpSocial = new AppSettings();
            tmpSocial.SetSocialAppsFromText(SocialAppsTextBox.Text);
            if (!tmpSocial.SocialAppProcesses.SequenceEqual(settings.SocialAppProcesses))
                return true;

            if ((int)AudioOffsetSlider.Value != settings.AudioOffsetMs)
                return true;

            string currentQuality = QualityCombo.SelectedIndex switch
            {
                0 => "Low",
                2 => "High",
                _ => "Medium"
            };
            if (!string.Equals(currentQuality, settings.VideoQuality, StringComparison.OrdinalIgnoreCase))
                return true;

            string currentAudioQuality = AudioQualityCombo.SelectedIndex switch
            {
                1 => "High",
                2 => "Studio",
                _ => "Standard"
            };
            if (!string.Equals(currentAudioQuality, settings.AudioQuality, StringComparison.OrdinalIgnoreCase))
                return true;

            int currentFps = ParseFpsInput(FpsCombo.SelectedItem?.ToString(), settings.CaptureFps);
            if (currentFps != settings.CaptureFps)
                return true;

            string currentRes = NormalizeResolutionInput(ResolutionCombo.SelectedItem?.ToString());
            if (!string.Equals(currentRes, settings.CaptureResolution, StringComparison.OrdinalIgnoreCase))
                return true;

            string currentGpu = GpuCombo.SelectedItem?.ToString() ?? "Auto";
            if (!string.Equals(currentGpu, settings.RecordingGpu, StringComparison.OrdinalIgnoreCase))
                return true;

            string currentCodec = VideoCodecCombo.SelectedIndex switch
            {
                1 => "H.265",
                2 => "AV1",
                _ => "H.264"
            };
            if (!string.Equals(currentCodec, settings.VideoCodec, StringComparison.OrdinalIgnoreCase))
                return true;
            if (CustomBitrateCheck.IsChecked != settings.CustomBitrateEnabled)
                return true;
            if (CustomBitrateCheck.IsChecked == true && (int)CustomBitrateSlider.Value != settings.CustomVideoBitrateMbps)
                return true;

            if (!string.Equals(RecordMonitorComboToSetting(), settings.RecordMonitor, StringComparison.OrdinalIgnoreCase))
                return true;

            if (StartupCheck.IsChecked != settings.LaunchOnStartup)
                return true;

            string currentMode = RecordingModeCombo.SelectedIndex switch
            {
                1 => "AlwaysOn",
                2 => "Manual",
                _ => "Auto"
            };
            if (!string.Equals(currentMode, settings.RecordingMode, StringComparison.OrdinalIgnoreCase))
                return true;
            if (ImportGamesCheck.IsChecked != settings.ImportInstalledGames)
                return true;
            if (AutoRecordNotifyCheck.IsChecked != settings.AutoRecordNotify)
                return true;
            var tmpAlways = new AppSettings();
            tmpAlways.SetAlwaysRecordFromText(AlwaysRecordTextBox.Text);
            if (!tmpAlways.AlwaysRecordProcesses.SequenceEqual(settings.AlwaysRecordProcesses))
                return true;
            var tmpNever = new AppSettings();
            tmpNever.SetNeverRecordFromText(NeverRecordTextBox.Text);
            if (!tmpNever.NeverRecordProcesses.SequenceEqual(settings.NeverRecordProcesses))
                return true;

            if (VoiceTriggerCheck.IsChecked != settings.VoiceTriggerEnabled)
                return true;

            if (VoiceTriggerCheck.IsChecked == true)
            {
                string currentLang = (VoiceLangCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "en";
                if (!string.Equals(currentLang, settings.VoiceTriggerLanguage, StringComparison.OrdinalIgnoreCase))
                    return true;

                string phrase = VoicePhraseBox.Text.Trim();
                string defaultPhrase = currentLang == "pl" ? "zapisz klip" : "clip that for me";
                string savedPhrase = settings.VoiceTriggerPhrase;
                string phraseToCompare = string.IsNullOrWhiteSpace(phrase) ? defaultPhrase : phrase;
                if (!string.Equals(phraseToCompare, savedPhrase, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // Capture Sound settings dirty check
            if (CaptureSoundCheck.IsChecked != settings.CaptureSoundEnabled)
                return true;
            if (CaptureSoundCheck.IsChecked == true && (int)CaptureSoundVolumeSlider.Value != settings.CaptureSoundVolume)
                return true;

            // Kill detection dirty check
            if (KillDetectionCheck.IsChecked != settings.KillDetectionEnabled)
                return true;
            if (KillDetectionCheck.IsChecked == true)
            {
                if (KillLolCheck.IsChecked != settings.KillDetectLol)
                    return true;
                if (KillCs2Check.IsChecked != settings.KillDetectCs2)
                    return true;
                if (KillValorantCheck.IsChecked != settings.KillDetectValorant)
                    return true;
                if (KillOverwatchCheck.IsChecked != settings.KillDetectOverwatch)
                    return true;
                if (KillMarkersCheck.IsChecked != settings.KillMarkersEnabled)
                    return true;
                if (KillAutoClipCheck.IsChecked != settings.KillAutoClipEnabled)
                    return true;
                if (KillAutoClipCheck.IsChecked == true && (int)KillAutoClipDelaySlider.Value != settings.KillAutoClipDelaySeconds)
                    return true;
                if (KillAutoClipCheck.IsChecked == true && (int)KillAutoClipCooldownSlider.Value != settings.KillAutoClipCooldownSeconds)
                    return true;
                if (KillCs2Check.IsChecked == true && ParseCs2PortInput() != settings.Cs2GsiPort)
                    return true;
                if (KillDiagCheck.IsChecked != settings.KillDetectionDiagnostics)
                    return true;
            }

            return false;
        }

        private bool ConfirmDiscardSettingsChanges()
        {
            if (HasUnsavedSettingsChanges())
            {
                var result = CustomMessageBox.Show(
                    "You have unsaved changes in settings. Are you sure you want to discard them?",
                    "Discard Changes",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.No)
                {
                    return false;
                }

                if (_isRebinding)
                {
                    _app.HookManager.HotkeyReboundLive -= HookManager_HotkeyReboundLive;
                    _app.HookManager.HotkeyRebound -= HookManager_HotkeyRebound;
                    _app.HookManager.StopRebindingMode();
                    _isRebinding = false;
                }
                LoadCurrentSettings();
            }
            return true;
        }

        private void SetStartupRegistry(bool start)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        string appName = "Wisp";
                        string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                        if (string.IsNullOrEmpty(exePath) || !exePath.EndsWith(".exe"))
                        {
                            exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Wisp.exe");
                        }

                        if (start)
                        {
                            key.SetValue(appName, $"\"{exePath}\" --startup");
                            Logger.Info($"Registered startup run key: {exePath}");
                        }
                        else
                        {
                            key.DeleteValue(appName, false);
                            Logger.Info("Deleted startup run key.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to set Windows startup registry key.", ex);
            }
        }

        private void VisualNotifPosBtn_Click(object sender, RoutedEventArgs e)
        {
            int monitorIndex = NotifMonitorCombo.SelectedIndex >= 0 ? NotifMonitorCombo.SelectedIndex : 0;
            var editor = new NotificationPositionEditor(monitorIndex, _pendingNotifPosPctX, _pendingNotifPosPctY) { Owner = this };
            if (editor.ShowDialog() == true)
            {
                _pendingNotifPosPctX = editor.ResultPctX;
                _pendingNotifPosPctY = editor.ResultPctY;
                ShowToast("Position updated", "Save settings to apply changes.", ToastKind.Info);
            }
        }

        // ===== New Delight (D1 & D2) Event Handlers =====
        private void CaptureSoundCheck_Changed(object sender, RoutedEventArgs e)
        {
            UpdateCaptureSoundPanelVisibility();
        }

        private void UpdateCaptureSoundPanelVisibility()
        {
            if (CaptureSoundVolumePanel != null)
            {
                CaptureSoundVolumePanel.Visibility = CaptureSoundCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void CaptureSoundVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (CaptureSoundVolumeText != null)
            {
                CaptureSoundVolumeText.Text = $"{(int)CaptureSoundVolumeSlider.Value}%";
            }
        }

        private void ReRunSetupBtn_Click(object sender, RoutedEventArgs e)
        {
            var result = CustomMessageBox.Show(
                "This will re-run the setup wizard to reconfigure Wisp. Your current settings will be backed up. Continue?",
                "Re-run Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _app.StopBackgroundRecording();
                _app.HookManager.UnregisterHotkey();

                var onboarding = new OnboardingWindow(_app.Settings) { Owner = this };
                if (onboarding.ShowDialog() == true)
                {
                    _app.Settings.HasCompletedOnboarding = true;
                    _app.Settings.Save();
                    ShowToast("Setup completed", "Your new configuration has been saved.", ToastKind.Success);
                }
                else
                {
                    ShowToast("Setup cancelled", "Your previous configuration remains active.", ToastKind.Info);
                }

                LoadCurrentSettings();
                _app.HookManager.RegisterHotkey(_app.Settings.GetHotkeyKeysList());
                _app.StartBackgroundRecording();
            }
        }

        private void TargetCursorCheck_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (TargetCursor == null) return;
                if (TargetCursorCheck.IsChecked == true)
                    TargetCursor.Enable();
                else
                    TargetCursor.Disable();
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error toggling cursor:\n{ex.Message}\n\n{ex.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
