using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Wisp
{
    public partial class MainWindow
    {
        // ================= PLUGINS TAB =================

        private void PluginsTab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!LeavePlayerForTabSwitch()) return;
            if (!ConfirmDiscardSettingsChanges()) return;
            SwitchToPluginsTab();
        }

        public void SwitchToPluginsTab()
        {
            ResetAllViews();

            PluginsTab.Tag = "Selected";
            PluginsHeader.Visibility = Visibility.Visible;

            FadeInElement(PluginsGrid);
            RefreshPluginList();
        }

        private void RefreshPluginList()
        {
            var pm = ((App)Application.Current).Plugins;
            if (pm == null)
            {
                PluginsItemsControl.ItemsSource = null;
                PluginsEmptyState.Visibility = Visibility.Visible;
                return;
            }

            var plugins = pm.Plugins; // internal, same assembly - fine
            var items = plugins.Select(lp => new PluginListItem
            {
                Id = lp.Id,
                Name = lp.Manifest.Name,
                VersionLabel = $"v{lp.Manifest.Version}",
                AuthorLabel = string.IsNullOrWhiteSpace(lp.Manifest.Author) ? "" : $"by {lp.Manifest.Author}",
                Description = lp.Manifest.Description ?? "",
                Status = lp.Status,
                IsEnabled = lp.Enabled,
                HasSettings = lp.Enabled && lp.Instance?.GetSettings()?.Count > 0
            }).ToList();

            if (items.Count == 0)
            {
                PluginsItemsControl.ItemsSource = null;
                PluginsEmptyState.Visibility = Visibility.Visible;
            }
            else
            {
                PluginsEmptyState.Visibility = Visibility.Collapsed;
                PluginsItemsControl.ItemsSource = items;
            }
        }

        private void PluginToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox cb) return;
            string? id = cb.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(id)) return;

            var pm = ((App)Application.Current).Plugins;
            if (pm == null) return;

            if (cb.IsChecked == true)
                pm.Enable(id);
            else
                pm.Disable(id);

            RefreshPluginList();
        }

        private void OpenPluginsFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            var pm = ((App)Application.Current).Plugins;
            if (pm == null) return;

            try
            {
                Directory.CreateDirectory(pm.PluginsRootPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = pm.PluginsRootPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open plugins folder: {ex.Message}");
            }
        }

        private void ReloadPluginsBtn_Click(object sender, RoutedEventArgs e)
        {
            var pm = ((App)Application.Current).Plugins;
            if (pm == null) return;

            pm.ReloadAll();
            RefreshPluginList();

            ShowToast("Plugins reloaded", null, ToastKind.Info);
        }

        private void PluginSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string? id = btn.Tag?.ToString();
            if (string.IsNullOrWhiteSpace(id)) return;

            var pm = ((App)Application.Current).Plugins;
            var lp = pm?.Plugins.FirstOrDefault(p => p.Id == id);
            
            // Only enabled plugins have loaded instances
            if (lp == null || !lp.Enabled || lp.Instance == null) return;

            var fields = lp.Instance.GetSettings();
            if (fields == null || fields.Count == 0) return;

            var dialog = new PluginSettingsDialog(lp.Manifest.Name ?? lp.Id, fields)
            {
                Owner = this
            };

            dialog.ShowDialog();

            if (dialog.ResultValues != null)
            {
                try
                {
                    lp.Instance.OnSettingsSaved(dialog.ResultValues);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Plugin {lp.Id} threw during OnSettingsSaved: {ex}");
                    ShowToast("Plugin Error", "Failed to save settings.", ToastKind.Error);
                }
            }
        }
    }

    /// <summary>
    /// View model for a single plugin card in the Plugins tab.
    /// </summary>
    public class PluginListItem : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private bool _hasSettings;

        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string VersionLabel { get; set; } = "";
        public string AuthorLabel { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "";

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
            }
        }

        public bool HasSettings
        {
            get => _hasSettings;
            set
            {
                if (_hasSettings == value) return;
                _hasSettings = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
