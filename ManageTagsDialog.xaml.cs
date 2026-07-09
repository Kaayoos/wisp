using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Wisp.Models;
using Wisp.Services;

namespace Wisp
{
    public partial class ManageTagsDialog : Window
    {
        private readonly DatabaseService _db;
        private List<TagDefinition> _allTags = new();
        private TagDefinition? _selectedTag;
        private string _selectedColorHex = "#00F2FF"; // Default to Wisp Cyan

        // 10 premium curated colors
        private static readonly string[] CuratedColors = new[]
        {
            "#00F2FF", // Wisp Cyan
            "#10B981", // Neon Emerald
            "#D946EF", // Electric Orchid
            "#F97316", // Sunset Amber
            "#EF4444", // Ruby Flare
            "#8B5CF6", // Lavender Fog
            "#3B82F6", // Ocean Depth
            "#34D399", // Mint Glow
            "#F43F5E", // Rose Velvet
            "#EAB308"  // Gold Dust
        };

        private readonly List<Border> _swatches = new();

        public ManageTagsDialog()
        {
            InitializeComponent();
            _db = ((App)Application.Current).DbService;
            LoadTags();
            BuildColorSwatches();
            SetCreateMode();
        }

        private void LoadTags()
        {
            try
            {
                _allTags = _db.GetAllTagDefinitions();
                TagsListBox.ItemsSource = null;
                TagsListBox.ItemsSource = _allTags;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load tags in dialog: {ex.Message}");
            }
        }

        private void BuildColorSwatches()
        {
            ColorSwatchesGrid.Children.Clear();
            _swatches.Clear();

            foreach (var hex in CuratedColors)
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var inner = new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(9),
                    Background = new SolidColorBrush(color),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var outer = new Border
                {
                    Width = 26,
                    Height = 26,
                    CornerRadius = new CornerRadius(13),
                    BorderThickness = new Thickness(1.5),
                    BorderBrush = Brushes.Transparent,
                    Margin = new Thickness(3),
                    Cursor = Cursors.Hand,
                    Tag = hex,
                    Child = inner
                };

                outer.MouseLeftButtonUp += Swatch_Click;
                ColorSwatchesGrid.Children.Add(outer);
                _swatches.Add(outer);
            }
        }

        private void Swatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border b && b.Tag is string hex)
            {
                SelectColor(hex);
            }
        }

        private void SelectColor(string hex)
        {
            _selectedColorHex = hex;
            foreach (var swatch in _swatches)
            {
                if (swatch.Tag is string sHex && string.Equals(sHex, hex, StringComparison.OrdinalIgnoreCase))
                {
                    swatch.BorderBrush = Brushes.White;
                }
                else
                {
                    swatch.BorderBrush = Brushes.Transparent;
                }
            }
        }

        private void SetCreateMode()
        {
            _selectedTag = null;
            TagsListBox.SelectedIndex = -1;
            EditorHeader.Text = "CREATE NEW TAG";
            TagNameTextBox.Text = "";
            SaveTagBtn.Content = "Create Tag";
            DeleteTagBtn.Visibility = Visibility.Collapsed;
            SelectColor("#00F2FF"); // Default to Cyan
        }

        private void SetEditMode(TagDefinition tag)
        {
            _selectedTag = tag;
            EditorHeader.Text = "EDIT TAG";
            TagNameTextBox.Text = tag.Name;
            SaveTagBtn.Content = "Update Tag";
            DeleteTagBtn.Visibility = Visibility.Visible;
            SelectColor(tag.ColorHex);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Entrance animation (same as RenameDialog)
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

        private void TagsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TagsListBox.SelectedItem is TagDefinition selected)
            {
                SetEditMode(selected);
            }
        }

        private void AddNewTagBtn_Click(object sender, RoutedEventArgs e)
        {
            SetCreateMode();
        }

        private void SaveTagBtn_Click(object sender, RoutedEventArgs e)
        {
            string name = TagNameTextBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                CustomMessageBox.Show(this, "Please enter a tag name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Check if name contains commas (not allowed because it's stored as CSV)
            if (name.Contains(","))
            {
                CustomMessageBox.Show(this, "Tag names cannot contain commas.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                if (_selectedTag == null)
                {
                    // Create mode
                    if (_allTags.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        CustomMessageBox.Show(this, "A tag with that name already exists.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    var newTag = new TagDefinition { Name = name, ColorHex = _selectedColorHex };
                    _db.SaveTagDefinition(newTag);
                    LoadTags();
                    SetCreateMode();
                }
                else
                {
                    // Edit mode
                    string oldName = _selectedTag.Name;
                    bool nameChanged = !string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase);

                    if (nameChanged && _allTags.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        CustomMessageBox.Show(this, "A tag with that name already exists.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    if (nameChanged)
                    {
                        // Rename updates tag definition AND all associated clips' CSV string!
                        _db.RenameTagDefinition(oldName, name);
                    }
                    
                    // Save color/update
                    var updated = new TagDefinition { Name = name, ColorHex = _selectedColorHex };
                    _db.SaveTagDefinition(updated);

                    LoadTags();
                    
                    // Select edited tag again
                    var reselected = _allTags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (reselected != null)
                    {
                        TagsListBox.SelectedItem = reselected;
                    }
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show(this, $"Failed to save tag: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void DeleteTagBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTag == null) return;

            string name = _selectedTag.Name;
            var confirm = CustomMessageBox.Show(this, $"Are you sure you want to delete tag \"{name}\"? This tag will be removed from all gameplay clips.", "Delete Tag", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            
            if (confirm == MessageBoxResult.Yes)
            {
                try
                {
                    _db.DeleteTagDefinition(name);
                    LoadTags();
                    SetCreateMode();
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Show(this, $"Failed to delete tag: {ex.Message}", "Database Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void DoneBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void TagNameTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            TagNameTextBox.SelectAll();
        }
    }
}
