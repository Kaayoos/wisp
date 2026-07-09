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
        public void LoadClips()
        {
            try
            {
                _allClips = _app.DbService.GetAllClips();
                _app.DbService.EnsureDefaultTagsExist(_allClips);
                _allClips = _app.DbService.GetAllClips(); // reload to get resolved tag definitions populated in TagList
                PopulateGameFilter();
                PopulateTagFilter();
                ApplyFilterAndSort();

                // Keep the card shield flag + (if visible) the Storage stats in sync after any reload,
                // including the reload an auto-deletion sweep triggers.
                UpdateAutoDeletionActive();
                if (StorageGrid != null && StorageGrid.Visibility == Visibility.Visible)
                    RefreshStorageView();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load clips: {ex.Message}");
            }
        }

        public void UpdateRecordingIndicator()
        {
            bool recording = _app.RecorderService.IsRecording;
            if (ToggleRecBtn != null)
                ToggleRecBtn.Content = recording ? "Stop Replay" : "Start Replay";
            UpdateBufferStatusVisual(recording);
        }

        // Drives the capture console's status light. While Instant Replay is live the dot BREATHES (a slow
        // opacity pulse) and the label reads REPLAY ON in the accent; when paused the dot goes dim and
        // steady and the label reads PAUSED in muted ink. State is carried by motion + label + brightness,
        // never hue alone, so it still reads at a glance on any user-themed accent. SetResourceReference
        // re-binds the live label to {AccentBrush} so it keeps tracking accent re-themes after a pause.
        private void UpdateBufferStatusVisual(bool recording)
        {
            if (BufferStatusDot == null || BufferStatusText == null) return;

            if (recording)
            {
                BufferStatusText.Text = "REPLAY ON";
                BufferStatusText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

                var pulse = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.3,
                    Duration = new Duration(TimeSpan.FromSeconds(1.25)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                BufferStatusDot.BeginAnimation(UIElement.OpacityProperty, pulse);
            }
            else
            {
                BufferStatusText.Text = "PAUSED";
                if (TryFindResource("TextMutedBrush") is Brush muted)
                    BufferStatusText.Foreground = muted;

                BufferStatusDot.BeginAnimation(UIElement.OpacityProperty, null); // release the pulse
                BufferStatusDot.Opacity = 0.28;
            }
        }

        private void UpdateCaptureTarget()
        {
            if (!_app.RecorderService.IsRecording)
            {
                CaptureTargetText.Text = "Not recording";
                CaptureProcessText.Text = "";
                return;
            }

            var (title, processName) = FFmpegRecorderService.GetForegroundWindowInfo();
            CaptureTargetText.Text = title;
            CaptureProcessText.Text = string.IsNullOrEmpty(processName) ? "" : processName + ".exe";
        }

        private void ApplyFilterAndSort()
        {
            // Guard: this can fire during InitializeComponent before all controls are created
            if (SearchBox == null || SortCombo == null || ClipsListBox == null || EmptyStateGrid == null || GameFilterCombo == null) return;

            string query = SearchBox.Text.Trim().ToLower();

            // 1. Filter
            var filtered = _allClips.AsEnumerable();
            if (!string.IsNullOrEmpty(query))
            {
                filtered = filtered.Where(c => c.Filename.ToLower().Contains(query) || (c.GameName ?? "").ToLower().Contains(query));
            }

            // Game filter
            if (GameFilterCombo.SelectedItem is string selectedGame && selectedGame != AllGamesLabel)
            {
                filtered = filtered.Where(c => string.Equals(c.GameName, selectedGame, StringComparison.OrdinalIgnoreCase));
            }

            // Tag filter
            if (TagFilterCombo != null && TagFilterCombo.SelectedItem is string selectedTag && selectedTag != AllTagsLabel)
            {
                filtered = filtered.Where(c => 
                    !string.IsNullOrEmpty(c.Tags) && 
                    c.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                          .Select(t => t.Trim())
                          .Any(t => string.Equals(t, selectedTag, StringComparison.OrdinalIgnoreCase))
                );
            }

            // Favorites filter
            if (_showOnlyFavorites)
            {
                filtered = filtered.Where(c => c.IsFavorite);
            }

            // 2. Sort
            int sortIndex = SortCombo.SelectedIndex;
            filtered = sortIndex switch
            {
                1 => filtered.OrderBy(c => c.CreatedAt),            // Oldest First
                2 => filtered.OrderByDescending(c => c.DurationSeconds), // Longest First
                3 => filtered.OrderBy(c => c.DurationSeconds),           // Shortest First
                _ => filtered.OrderByDescending(c => c.CreatedAt)    // Newest First
            };

            var list = filtered.ToList();
            ClipsListBox.ItemsSource = list;
            
            // Empty State Visibility
            if (list.Count == 0)
            {
                ClipsListBox.Visibility = Visibility.Collapsed;
                EmptyStateGrid.Visibility = Visibility.Visible;
            }
            else
            {
                ClipsListBox.Visibility = Visibility.Visible;
                EmptyStateGrid.Visibility = Visibility.Collapsed;
            }
        }

        // Rebuilds the game filter dropdown from the current clips, preserving the selection if possible.
        private void PopulateGameFilter()
        {
            if (GameFilterCombo == null) return;

            string? previous = GameFilterCombo.SelectedItem as string;

            var games = _allClips
                .Select(c => c.GameName)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _suppressGameFilter = true;
            GameFilterCombo.Items.Clear();
            GameFilterCombo.Items.Add(AllGamesLabel);
            foreach (var g in games) GameFilterCombo.Items.Add(g);

            if (previous != null && GameFilterCombo.Items.Contains(previous))
                GameFilterCombo.SelectedItem = previous;
            else
                GameFilterCombo.SelectedIndex = 0;
            _suppressGameFilter = false;
        }

        private void GameFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressGameFilter) return;
            ApplyFilterAndSort();
        }

        private void PopulateTagFilter()
        {
            if (TagFilterCombo == null) return;

            string? previous = TagFilterCombo.SelectedItem as string;

            var tags = _allClips
                .Where(c => !string.IsNullOrWhiteSpace(c.Tags))
                .SelectMany(c => c.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _suppressTagFilter = true;
            TagFilterCombo.Items.Clear();
            TagFilterCombo.Items.Add(AllTagsLabel);
            foreach (var t in tags) TagFilterCombo.Items.Add(t);

            if (previous != null && TagFilterCombo.Items.Contains(previous))
                TagFilterCombo.SelectedItem = previous;
            else
                TagFilterCombo.SelectedIndex = 0;
            _suppressTagFilter = false;
        }

        private void TagFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressTagFilter) return;
            ApplyFilterAndSort();
        }

        private void FavoriteCardBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Clip clip)
                ToggleFavorite(clip);
        }

        private void ProtectCardBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Clip clip)
                ToggleProtected(clip);
        }

        private void ProtectContext_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedClip() is Clip clip)
                ToggleProtected(clip);
        }

        /// <summary>Flips a clip's "keep / protect from auto-deletion" shield. Favorites are always kept, so
        /// toggling protection on a favorite is a no-op (with a hint).</summary>
        private void ToggleProtected(Clip clip)
        {
            if (clip == null) return;
            if (clip.IsFavorite)
            {
                ShowToast("Already kept", "Favorited clips are always kept, so they're never auto-deleted.", ToastKind.Info);
                return;
            }

            clip.ProtectedFromDeletion = !clip.ProtectedFromDeletion; // shield updates live via IsKept
            try
            {
                _app.DbService.SetProtectedFromDeletion(clip.Id, clip.ProtectedFromDeletion);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update protection: {ex.Message}");
            }
        }

        private void PlayerProtectBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_activeClip != null) ToggleProtected(_activeClip);
        }

        private void ToggleFavorite(Clip clip)
        {
            clip.IsFavorite = !clip.IsFavorite; // star updates live via INotifyPropertyChanged

            try
            {
                _app.DbService.SetFavorite(clip.Id, clip.IsFavorite);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to update favorite: {ex.Message}");
            }

            // If favorites-only is active, a clip that was just unfavorited should drop out of view.
            if (_showOnlyFavorites && !clip.IsFavorite)
                ApplyFilterAndSort();
        }


        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // Spin the glyph so the click visibly registers, then reload and confirm.
            if (RefreshIconRotate != null)
            {
                var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.6))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
                };
                RefreshIconRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
            }

            LoadClips();

            int count = _allClips?.Count ?? 0;
            ShowToast("Library refreshed", count == 1 ? "1 clip" : $"{count} clips", ToastKind.Success);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilterAndSort();
        }

        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilterAndSort();
        }

        // ================= LIST EVENT HANDLERS =================
        private void ClipsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ClipsListBox.SelectedItems.Count > 1)
            {
                BulkActionBar.Visibility = Visibility.Visible;
                BulkSelectionCountText.Text = $"{ClipsListBox.SelectedItems.Count} selected";
            }
            else
            {
                BulkActionBar.Visibility = Visibility.Collapsed;
            }
        }

        private void BulkFavoriteBtn_Click(object sender, RoutedEventArgs e)
        {
            var selectedClips = ClipsListBox.SelectedItems.Cast<Clip>().ToList();
            if (selectedClips.Count == 0) return;

            bool anyNotFavorite = selectedClips.Any(c => !c.IsFavorite);

            foreach (var clip in selectedClips)
            {
                clip.IsFavorite = anyNotFavorite; 
                try
                {
                    _app.DbService.SetFavorite(clip.Id, clip.IsFavorite);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to bulk update favorite: {ex.Message}");
                }
            }

            if (_showOnlyFavorites && !anyNotFavorite)
                ApplyFilterAndSort();

            BulkActionBar.Visibility = Visibility.Collapsed;
            ClipsListBox.UnselectAll();
        }

        private void BulkTagsBtn_Click(object sender, RoutedEventArgs e)
        {
            var selectedClips = ClipsListBox.SelectedItems.Cast<Clip>().ToList();
            if (selectedClips.Count == 0) return;

            var btn = sender as Button;
            if (btn == null) return;

            var menu = new ContextMenu();
            
            try
            {
                var allTags = _app.DbService.GetAllTagDefinitions();
                
                foreach (var tag in allTags)
                {
                    bool allHaveIt = selectedClips.All(c => 
                        (c.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                                      .Select(t => t.Trim())
                                      .Contains(tag.Name, StringComparer.OrdinalIgnoreCase)
                    );

                    var item = new MenuItem
                    {
                        Header = tag.Name,
                        IsCheckable = true,
                        IsChecked = allHaveIt
                    };

                    var tagName = tag.Name;
                    var bHaveIt = allHaveIt;
                    item.Click += (s, ev) =>
                    {
                        foreach (var clip in selectedClips)
                        {
                            var parts = (clip.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                         .Select(t => t.Trim())
                                                         .ToList();
                            bool hasTag = parts.Contains(tagName, StringComparer.OrdinalIgnoreCase);
                            if (bHaveIt)
                            {
                                parts.RemoveAll(t => string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase));
                            }
                            else if (!hasTag)
                            {
                                parts.Add(tagName);
                            }

                            string newTags = string.Join(", ", parts);
                            clip.Tags = newTags;
                            _app.DbService.UpdateTags(clip.Id, newTags);
                        }

                        LoadClips();
                        BulkActionBar.Visibility = Visibility.Collapsed;
                        ClipsListBox.UnselectAll();
                    };

                    menu.Items.Add(item);
                }

                if (allTags.Count > 0)
                {
                    menu.Items.Add(new Separator());
                }

                var manageItem = new MenuItem { Header = "Manage tags..." };
                manageItem.Click += (s, ev) =>
                {
                    OpenManageTagsDialog();
                    BulkActionBar.Visibility = Visibility.Collapsed;
                    ClipsListBox.UnselectAll();
                };
                menu.Items.Add(manageItem);

                menu.PlacementTarget = btn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open bulk tags menu: {ex.Message}");
            }
        }

        private void ClipContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is ContextMenu menu)
            {
                var clip = menu.DataContext as Clip;
                if (clip == null && ClipsListBox.SelectedItem is Clip selected)
                {
                    clip = selected;
                }

                if (clip == null) return;

                // Tailor the "protect from deletion" item to this clip + whether auto-deletion is on at all.
                foreach (var item in menu.Items)
                {
                    if (item is MenuItem pmi && (pmi.Tag as string) == "protect")
                    {
                        pmi.Visibility = AutoDeletionActive ? Visibility.Visible : Visibility.Collapsed;
                        if (clip.IsFavorite)
                        {
                            pmi.Header = "Kept (favorite)";
                            pmi.IsEnabled = false;
                        }
                        else
                        {
                            pmi.Header = clip.ProtectedFromDeletion ? "Stop keeping (allow deletion)" : "Keep this clip";
                            pmi.IsEnabled = true;
                        }
                        break;
                    }
                }

                InjectPluginClipActions(menu, clip);

                MenuItem? assignMenuItem = null;
                foreach (var item in menu.Items)
                {
                    if (item is MenuItem mi && mi.Header?.ToString()?.Contains("Assign Tags") == true)
                    {
                        assignMenuItem = mi;
                        break;
                    }
                }

                if (assignMenuItem == null) return;

                assignMenuItem.Items.Clear();

                try
                {
                    var allTags = _app.DbService.GetAllTagDefinitions();
                    var clipTags = (clip.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                     .Select(t => t.Trim())
                                                     .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var tag in allTags)
                    {
                        var item = new MenuItem
                        {
                            Header = tag.Name,
                            IsCheckable = true,
                            IsChecked = clipTags.Contains(tag.Name)
                        };
                        var currentClip = clip;
                        var tagName = tag.Name;
                        item.Click += (s, ev) =>
                        {
                            ToggleTagOnClip(currentClip, tagName);
                        };
                        assignMenuItem.Items.Add(item);
                    }

                    if (allTags.Count > 0)
                    {
                        assignMenuItem.Items.Add(new Separator());
                    }

                    var manageItem = new MenuItem { Header = "Manage tags..." };
                    manageItem.Click += (s, ev) =>
                    {
                        OpenManageTagsDialog();
                    };
                    assignMenuItem.Items.Add(manageItem);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to build Assign Tags submenu: {ex.Message}");
                }
            }
        }

        private const string PluginMenuTag = "__wisp_plugin_action__";

        /// <summary>
        /// Folds plugin-registered clip actions into a clip's context menu when it opens. Plugin items
        /// (and their leading separator) are tagged so the previous open's items are cleared first;
        /// actions are filtered by their optional CanShow predicate and invoked crash-isolated.
        /// </summary>
        private void InjectPluginClipActions(ContextMenu menu, Clip clip)
        {
            // Clear plugin items added on a previous open of this (reused) menu.
            for (int i = menu.Items.Count - 1; i >= 0; i--)
            {
                if (menu.Items[i] is FrameworkElement fe && (fe.Tag as string) == PluginMenuTag)
                    menu.Items.RemoveAt(i);
            }

            var actions = (Application.Current as App)?.GetPluginClipActions();
            if (actions == null || actions.Count == 0) return;

            var info = Services.Plugins.PluginMap.ToClipInfo(clip);

            var visible = new List<PluginClipActionEntry>();
            foreach (var entry in actions)
            {
                try
                {
                    if (entry.Action.CanShow == null || entry.Action.CanShow(info)) visible.Add(entry);
                }
                catch { /* a throwing predicate simply hides the item */ }
            }
            if (visible.Count == 0) return;

            menu.Items.Add(new Separator { Tag = PluginMenuTag });

            foreach (var entry in visible)
            {
                var mi = new MenuItem { Header = entry.Action.Label, Tag = PluginMenuTag };

                if (!string.IsNullOrWhiteSpace(entry.Action.IconGlyph))
                {
                    mi.Icon = new TextBlock
                    {
                        Text = Services.Plugins.GlyphUtil.ToGlyph(entry.Action.IconGlyph),
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        Foreground = (Brush)FindResource("AccentBrush"),
                        FontSize = 14
                    };
                }

                var capturedEntry = entry;
                mi.Click += (s, ev) =>
                {
                    try { capturedEntry.Action.Invoke(info); }
                    catch (Exception ex)
                    {
                        Logger.Error($"[Plugin:{capturedEntry.PluginId}] clip action '{capturedEntry.Action.Id}' threw.", ex);
                        ShowToast("Plugin error", $"“{capturedEntry.Action.Label}” failed.", ToastKind.Error);
                    }
                };
                menu.Items.Add(mi);
            }
        }

        private void ToggleTagOnClip(Clip clip, string tagName)
        {
            var parts = (clip.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                                         .Select(t => t.Trim())
                                         .ToList();
            bool hasTag = parts.Contains(tagName, StringComparer.OrdinalIgnoreCase);
            if (hasTag)
            {
                parts.RemoveAll(t => string.Equals(t, tagName, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                parts.Add(tagName);
            }

            string newTags = string.Join(", ", parts);
            clip.Tags = newTags;

            try
            {
                _app.DbService.UpdateTags(clip.Id, newTags);
                
                // Refresh local tag list on clip
                var tagDefs = _app.DbService.GetAllTagDefinitions().ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);
                var newList = new List<TagDefinition>();
                foreach (var p in parts)
                {
                    if (tagDefs.TryGetValue(p, out var td)) newList.Add(td);
                    else newList.Add(new TagDefinition { Name = p });
                }
                clip.TagList = newList;

                PopulateTagFilter();
                ApplyFilterAndSort();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to toggle tag on clip: {ex.Message}");
            }
        }

        private void OpenManageTagsDialog()
        {
            var dialog = new ManageTagsDialog { Owner = this };
            dialog.ShowDialog();
            LoadClips(); // Refresh the clip list and tag filters after edit
        }

        private void BulkDeleteBtn_Click(object sender, RoutedEventArgs e)
        {
            var selectedClips = ClipsListBox.SelectedItems.Cast<Clip>().ToList();
            if (selectedClips.Count == 0) return;

            var result = CustomMessageBox.Show(this, $"Are you sure you want to delete {selectedClips.Count} clips? This cannot be undone.", "Delete clips", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                foreach (var clip in selectedClips)
                {
                    try
                    {
                        // Use the canonical deleter so the per-source audio sidecars (system/mic/social
                        // .m4a tracks in %appdata%) are removed too. The old inline delete here only removed
                        // the video + thumbnail, leaving the audio tracks orphaned on bulk delete.
                        _app.DbService.DeleteClipAndFiles(clip);
                        _app.Plugins?.RaiseClipRemoved(clip, superseded: false);
                        _allClips.Remove(clip);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to delete clip {clip.Id}: {ex.Message}");
                    }
                }

                _app.DbService.CleanOrphanedClips();
                LoadClips();
            }

            BulkActionBar.Visibility = Visibility.Collapsed;
            ClipsListBox.UnselectAll();
        }

        private void BulkCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            BulkActionBar.Visibility = Visibility.Collapsed;
            ClipsListBox.UnselectAll();
        }

        private void ClipsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ClipsListBox.SelectedItem is Clip selectedClip)
            {
                PlayClip(selectedClip);
            }
        }

        // ================= CONTEXT MENU =================
        private void PlayContext_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedClip() is Clip clip) PlayClip(clip);
        }

        private void RenameContext_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedClip() is Clip clip)
            {
                string oldName = Path.GetFileNameWithoutExtension(clip.Filename);
                string extension = Path.GetExtension(clip.Filename);

                // Show custom input dialog
                var dialog = new RenameDialog(oldName) { Owner = this };
                if (dialog.ShowDialog() == true)
                {
                    string newNameWithoutExt = dialog.ResultText.Trim();
                    if (string.IsNullOrEmpty(newNameWithoutExt) || newNameWithoutExt == oldName) return;

                    // Clean name from illegal characters
                    foreach (char c in Path.GetInvalidFileNameChars())
                    {
                        newNameWithoutExt = newNameWithoutExt.Replace(c.ToString(), "");
                    }

                    string newFilename = newNameWithoutExt + extension;
                    string newFilePath = Path.Combine(Path.GetDirectoryName(clip.FilePath)!, newFilename);

                    try
                    {
                        if (File.Exists(newFilePath))
                        {
                            CustomMessageBox.Show("A file with that name already exists.", "Rename Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                            return;
                        }

                        File.Move(clip.FilePath, newFilePath);
                        _app.DbService.UpdateClipPaths(clip.Id, newFilePath, newFilename);
                        LoadClips();
                    }
                    catch (Exception ex)
                    {
                        CustomMessageBox.Show($"Failed to rename file: {ex.Message}", "Rename Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private void DeleteContext_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedClip() is Clip clip)
            {
                DeleteClip(clip);
            }
        }

        private void DeleteClip(Clip clip)
        {
            var result = CustomMessageBox.Show($"Are you sure you want to permanently delete \"{clip.Filename}\"?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    // 1. Delete video file
                    if (File.Exists(clip.FilePath))
                    {
                        File.Delete(clip.FilePath);
                    }

                    // 2. Delete thumbnail file
                    if (File.Exists(clip.ThumbnailPath))
                    {
                        File.Delete(clip.ThumbnailPath);
                    }

                    // 2b. Delete the separate audio-track sidecars
                    try { if (!string.IsNullOrEmpty(clip.SystemTrackPath) && File.Exists(clip.SystemTrackPath)) File.Delete(clip.SystemTrackPath); } catch { }
                    try { if (!string.IsNullOrEmpty(clip.MicTrackPath) && File.Exists(clip.MicTrackPath)) File.Delete(clip.MicTrackPath); } catch { }
                    try { if (!string.IsNullOrEmpty(clip.SocialTrackPath) && File.Exists(clip.SocialTrackPath)) File.Delete(clip.SocialTrackPath); } catch { }

                    // 3. Delete from DB
                    _app.DbService.DeleteClip(clip.Id);

                    // 3b. Tell plugins so they can drop any per-clip data/files they kept (e.g. a camera track).
                    _app.Plugins?.RaiseClipRemoved(clip, superseded: false);

                    // 4. Reload
                    LoadClips();
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Show($"Failed to delete file: {ex.Message}", "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void PlayCardBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Clip clip)
            {
                PlayClip(clip);
            }
        }

        private void DeleteCardBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Clip clip)
            {
                DeleteClip(clip);
            }
        }

        private void OpenFolderContext_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedClip() is Clip clip)
            {
                if (File.Exists(clip.FilePath))
                {
                    Process.Start("explorer.exe", $"/select,\"{clip.FilePath}\"");
                }
                else
                {
                    Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(clip.FilePath)}\"");
                }
            }
        }

        private void CopyPathContext_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedClip() is Clip clip)
            {
                try
                {
                    Clipboard.SetText(clip.FilePath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to copy to clipboard: {ex.Message}");
                }
            }
        }

        private Clip? GetSelectedClip()
        {
            return ClipsListBox.SelectedItem as Clip;
        }

    }
}
