using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wisp.Models;
using Wisp.Services;

namespace Wisp
{
    public partial class MainWindow : Window
    {
        // ================= STORAGE TAB =================
        // Library/disk stats + the auto-deletion config (age limit and/or size budget). The actual deletion
        // engine lives in DatabaseService (CleanupOldClips / EnforceStorageCap) and is driven by
        // App.RunAutoDeletionSweep; this view just reports usage and edits the settings that engine reads.

        // True when either auto-deletion limit is on. The per-clip "Keep" pin on library cards (and in the
        // player) binds its visibility to this, so the pin only appears when keeping a clip actually matters.
        public static readonly DependencyProperty AutoDeletionActiveProperty =
            DependencyProperty.Register(nameof(AutoDeletionActive), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

        public bool AutoDeletionActive
        {
            get => (bool)GetValue(AutoDeletionActiveProperty);
            set => SetValue(AutoDeletionActiveProperty, value);
        }

        // True when the age limit is on AND the user kept the countdown badge enabled. Library cards bind
        // the "auto-deletes in X days" badge's visibility to this (combined with the clip's IsKept).
        public static readonly DependencyProperty RetentionCountdownActiveProperty =
            DependencyProperty.Register(nameof(RetentionCountdownActive), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

        public bool RetentionCountdownActive
        {
            get => (bool)GetValue(RetentionCountdownActiveProperty);
            set => SetValue(RetentionCountdownActiveProperty, value);
        }

        // The applied age limit (days). The countdown badge reads this to compute each clip's days-left, so
        // it mirrors the SAVED setting, not the in-progress slider.
        public static readonly DependencyProperty RetentionDaysProperty =
            DependencyProperty.Register(nameof(RetentionDays), typeof(int), typeof(MainWindow), new PropertyMetadata(0));

        public int RetentionDays
        {
            get => (int)GetValue(RetentionDaysProperty);
            set => SetValue(RetentionDaysProperty, value);
        }

        // Guards the config controls while RefreshStorageView populates them, so loading a value doesn't
        // read as a user edit (which would, e.g., redraw the donut mid-load).
        private bool _loadingStorageConfig;

        private void StorageTab_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!LeavePlayerForTabSwitch()) return;
            if (!ConfirmDiscardSettingsChanges()) return;
            SwitchToStorageTab();
        }

        public void SwitchToStorageTab()
        {
            ResetAllViews();

            StorageTab.Tag = "Selected";
            StorageHeader.Visibility = Visibility.Visible;

            RefreshStorageView();
            FadeInElement(StorageGrid);
        }

        /// <summary>Keeps the window-level flags that drive the card overlays (the Keep pin + the
        /// "auto-deletes in X days" countdown badge) in sync with the saved settings.</summary>
        public void UpdateAutoDeletionActive()
        {
            var s = _app.Settings;
            AutoDeletionActive = s.RetentionEnabled || s.MaxStorageEnabled;
            RetentionCountdownActive = s.RetentionEnabled && s.ShowDeletionCountdown;
            RetentionDays = s.RetentionDays;
        }

        /// <summary>Recomputes the library stats, disk usage, donut gauge, and (re)loads the config controls.</summary>
        public void RefreshStorageView()
        {
            if (StorageGrid == null) return;

            var clips = _allClips ?? new List<Clip>();
            long usedBytes = clips.Sum(c => c.FileSizeBytes);
            var now = DateTime.Now;
            var monthStart = new DateTime(now.Year, now.Month, 1);

            if (StatClipsTotal != null) StatClipsTotal.Text = clips.Count.ToString();
            if (StatClipsMonth != null) StatClipsMonth.Text = clips.Count(c => c.CreatedAt >= monthStart).ToString();
            if (StatHours != null) StatHours.Text = $"{clips.Sum(c => c.DurationSeconds) / 3600.0:F1}h";
            if (StatUsed != null) StatUsed.Text = FormatBytes(usedBytes);

            UpdateDriveUsage();

            // Load the config controls from settings without firing the edit handlers.
            _loadingStorageConfig = true;
            try
            {
                var s = _app.Settings;

                AgeDeleteCheck.IsChecked = s.RetentionEnabled;
                AgeDaysSlider.Value = Math.Clamp(s.RetentionDays, (int)AgeDaysSlider.Minimum, (int)AgeDaysSlider.Maximum);
                AgeDaysValueText.Text = $"{(int)AgeDaysSlider.Value} days";
                AgeDaysPanel.Visibility = s.RetentionEnabled ? Visibility.Visible : Visibility.Collapsed;
                CountdownBadgeCheck.IsChecked = s.ShowDeletionCountdown;

                SizeDeleteCheck.IsChecked = s.MaxStorageEnabled;
                SizeGbSlider.Value = Math.Clamp(s.MaxStorageGB, SizeGbSlider.Minimum, SizeGbSlider.Maximum);
                SizeGbValueText.Text = $"{(int)SizeGbSlider.Value} GB";
                SizePanel.Visibility = s.MaxStorageEnabled ? Visibility.Visible : Visibility.Collapsed;
            }
            finally { _loadingStorageConfig = false; }

            UpdateDonut(usedBytes);
            UpdateAutoDeletionActive();
        }

        /// <summary>Used/free/total + proportional bar for the drive the clips are saved on (handles any drive letter).</summary>
        private void UpdateDriveUsage()
        {
            try
            {
                string folder = _app.Settings.OutputFolder;
                string? root = Path.GetPathRoot(folder);
                if (string.IsNullOrEmpty(root)) return;

                var drive = new DriveInfo(root);
                if (!drive.IsReady) return;

                long total = drive.TotalSize;
                long free = drive.AvailableFreeSpace;
                long used = Math.Max(0, total - free);

                if (DriveNameText != null) DriveNameText.Text = root;
                if (DriveUsedText != null) DriveUsedText.Text = FormatBytes(used);
                if (DriveFreeText != null) DriveFreeText.Text = FormatBytes(free);
                if (DriveTotalText != null) DriveTotalText.Text = FormatBytes(total);

                double usedFrac = total > 0 ? (double)used / total : 0;
                if (DriveUsedCol != null) DriveUsedCol.Width = new GridLength(Math.Max(0.0001, usedFrac), GridUnitType.Star);
                if (DriveFreeCol != null) DriveFreeCol.Width = new GridLength(Math.Max(0.0001, 1 - usedFrac), GridUnitType.Star);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Storage: drive usage read failed: {ex.Message}");
            }
        }

        /// <summary>Renders the used-vs-budget donut. Accent fill, amber as it fills, red at/over the cap.</summary>
        private void UpdateDonut(long usedBytes)
        {
            if (DonutFill == null) return;

            double capGb = SizeGbSlider?.Value ?? _app.Settings.MaxStorageGB;
            double capBytes = capGb * 1024.0 * 1024.0 * 1024.0;
            double frac = capBytes > 0 ? usedBytes / capBytes : 0;
            double shown = Math.Clamp(frac, 0, 1);

            // StrokeDashArray is measured in multiples of the pen thickness. One full revolution =
            // circumference / thickness; the fill is a single dash that fraction of a revolution long,
            // followed by a full-circle gap so the pattern never repeats around the ring.
            const double diameter = 132.0, thickness = 15.0;
            double units = Math.PI * diameter / thickness;
            DonutFill.StrokeDashArray = new DoubleCollection { shown * units, units };

            string key = frac >= 0.98 ? "ErrorBrush" : frac >= 0.8 ? "WarningBrush" : "AccentBrush";
            DonutFill.Stroke = (Brush)FindResource(key);

            if (DonutUsedText != null) DonutUsedText.Text = FormatBytes(usedBytes);
            if (DonutCapText != null) DonutCapText.Text = $"of {(int)capGb} GB";
            if (DonutWarnText != null)
            {
                if (frac > 1.0)
                {
                    DonutWarnText.Text = "Your clips are over this budget. Applying will remove the oldest unprotected clips to fit.";
                    DonutWarnText.Visibility = Visibility.Visible;
                }
                else DonutWarnText.Visibility = Visibility.Collapsed;
            }
        }

        // ───────────── config control handlers ─────────────

        private void AgeDeleteCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (AgeDaysPanel != null)
                AgeDaysPanel.Visibility = AgeDeleteCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AgeDaysSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (AgeDaysValueText != null) AgeDaysValueText.Text = $"{(int)AgeDaysSlider.Value} days";
        }

        private void SizeDeleteCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (SizePanel != null)
                SizePanel.Visibility = SizeDeleteCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SizeGbSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SizeGbValueText != null) SizeGbValueText.Text = $"{(int)SizeGbSlider.Value} GB";
            if (!_loadingStorageConfig) UpdateDonut(_allClips?.Sum(c => c.FileSizeBytes) ?? 0);
        }

        /// <summary>
        /// Persists the auto-deletion config. If the size cap is on and the library already exceeds it, the
        /// user confirms a destructive prune first; then the limits run immediately (off the UI thread).
        /// </summary>
        private void StorageApplyBtn_Click(object sender, RoutedEventArgs e)
        {
            var s = _app.Settings;
            bool ageOn = AgeDeleteCheck.IsChecked == true;
            int ageDays = (int)AgeDaysSlider.Value;
            bool sizeOn = SizeDeleteCheck.IsChecked == true;
            int sizeGb = (int)SizeGbSlider.Value;

            if (sizeOn)
            {
                long cap = App.GbToBytes(sizeGb);
                var (count, bytes) = _app.DbService.PreviewStorageCapPrune(cap);
                if (count > 0)
                {
                    var result = CustomMessageBox.Show(
                        $"Your clips currently exceed {sizeGb} GB.\n\nApplying this limit will permanently delete the {count} oldest unprotected clip(s) (~{FormatBytes(bytes)}) to fit.\n\nFavorited and protected clips are kept. Continue?",
                        "Apply storage limit", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (result != MessageBoxResult.Yes) return;
                }
            }

            s.RetentionEnabled = ageOn;
            s.RetentionDays = ageDays;
            s.ShowDeletionCountdown = CountdownBadgeCheck.IsChecked == true;
            s.MaxStorageEnabled = sizeOn;
            s.MaxStorageGB = sizeGb;
            s.Save();

            UpdateAutoDeletionActive();

            // Apply the limits now; the sweep reloads the library + these stats when it removes anything.
            _app.RunAutoDeletionSweep();

            ShowToast("Storage settings saved", "Auto-deletion limits applied.", ToastKind.Success);
            RefreshStorageView();
        }

        /// <summary>Human-readable byte size in binary units (e.g. "12.4 GB").</summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "0 GB";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes;
            int i = 0;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return i <= 1 ? $"{v:F0} {units[i]}" : $"{v:F1} {units[i]}";
        }
    }
}
