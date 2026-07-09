using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Wisp
{
    // Converters that drive the per-clip "auto-deletes in X days" badge on library cards. The badge is
    // only meaningful when the age limit is on (RetentionEnabled), the user left the countdown toggle on
    // (ShowDeletionCountdown), and the clip isn't kept. Days-left = RetentionDays - whole days since capture.

    /// <summary>Helper shared by the countdown converters: whole days remaining before a clip is eligible
    /// for age-based auto-deletion. Can be zero or negative when the clip is already due (pending the next
    /// sweep).</summary>
    internal static class RetentionMath
    {
        public static int DaysLeft(DateTime createdAt, int retentionDays)
        {
            int elapsed = (int)Math.Floor((DateTime.Now - createdAt).TotalDays);
            return retentionDays - elapsed;
        }
    }

    /// <summary>[CreatedAt, RetentionDays] -> short badge text, e.g. "5d left" / "1d left" / "due".</summary>
    public class RetentionCountdownTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (values.Length < 2 || values[0] is not DateTime created) return "";
                int days = System.Convert.ToInt32(values[1]);
                int left = RetentionMath.DaysLeft(created, days);
                if (left <= 0) return "due";
                if (left == 1) return "1d left";
                return $"{left}d left";
            }
            catch { return ""; }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>[CreatedAt, RetentionDays] -> brush. Muted normally, amber when the clip is about to go
    /// (≤2 days) so an imminent deletion stands out without alarming red on every card.</summary>
    public class RetentionCountdownBrushConverter : IMultiValueConverter
    {
        private static readonly SolidColorBrush Muted = Frozen(Color.FromRgb(0xB9, 0xCA, 0xCB));

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                if (values.Length < 2 || values[0] is not DateTime created) return Muted;
                int days = System.Convert.ToInt32(values[1]);
                int left = RetentionMath.DaysLeft(created, days);
                if (left <= 2)
                {
                    if (Application.Current?.TryFindResource("WarningBrush") is Brush warn) return warn;
                    return Frozen(Color.FromRgb(0xFF, 0xC5, 0x3D));
                }
                return Muted;
            }
            catch { return Muted; }
        }

        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>[RetentionCountdownActive, IsKept] -> Visibility. The badge shows only while the countdown
    /// feature is active and the clip is not kept (kept clips survive every sweep, so no countdown).</summary>
    public class RetentionBadgeVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            bool active = values.Length > 0 && values[0] is bool a && a;
            bool kept = values.Length > 1 && values[1] is bool k && k;
            return (active && !kept) ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
