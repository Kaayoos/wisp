using System;
using System.Windows.Media;

namespace Wisp.Models
{
    public class TagDefinition
    {
        public string Name { get; set; } = "";
        public string ColorHex { get; set; } = "#00F2FF"; // Default to accent cyan

        // Helper properties for easy data binding in XAML
        public Brush BackgroundBrush
        {
            get
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(ColorHex);
                    var brush = new SolidColorBrush(color);
                    brush.Opacity = 0.15; // Sleek modern semi-transparent background
                    brush.Freeze();
                    return brush;
                }
                catch
                {
                    return Brushes.Transparent;
                }
            }
        }

        public Brush BorderBrush
        {
            get
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(ColorHex);
                    var brush = new SolidColorBrush(color);
                    brush.Opacity = 0.8; // High visibility solid border
                    brush.Freeze();
                    return brush;
                }
                catch
                {
                    return Brushes.Gray;
                }
            }
        }

        public Brush TextBrush
        {
            get
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(ColorHex);
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
                catch
                {
                    return Brushes.White;
                }
            }
        }
    }
}
