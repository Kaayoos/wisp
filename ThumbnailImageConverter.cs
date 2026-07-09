using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Wisp
{
    /// <summary>
    /// Loads a thumbnail file path into a small, frozen <see cref="BitmapImage"/>.
    ///
    /// Binding a path straight to <c>Image.Source</c> makes WPF decode the JPEG at its native
    /// resolution and hold it in memory - a full 1080p thumbnail is ~8 MB decoded, and the clip
    /// gallery is not virtualized, so every card was paying that cost at once. Decoding at gallery
    /// width via <see cref="BitmapImage.DecodePixelWidth"/> cuts that by ~25x, and
    /// <see cref="BitmapCacheOption.OnLoad"/> releases the file handle immediately so the thumbnail
    /// can still be renamed/deleted while the app is running.
    /// </summary>
    public class ThumbnailImageConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        {
            string? path = value as string;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return null;

            int decodeWidth = 360;
            if (parameter != null && int.TryParse(parameter.ToString(), out int p) && p > 0)
                decodeWidth = p;

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;          // decode now, then release the file
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.DecodePixelWidth = decodeWidth;                  // decode at display size, not native
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();                                        // shareable + immutable
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
