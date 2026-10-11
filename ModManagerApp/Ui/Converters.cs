using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SMM
{
    internal static class Palette
    {
        public static readonly SolidColorBrush Dim = new((Color)ColorConverter.ConvertFromString("#949BAA"));
        public static readonly SolidColorBrush Text = new((Color)ColorConverter.ConvertFromString("#E7EAF0"));
        public static readonly SolidColorBrush Ok = new((Color)ColorConverter.ConvertFromString("#6FBF73"));
        public static readonly SolidColorBrush Warn = new((Color)ColorConverter.ConvertFromString("#D8A657"));
        public static readonly SolidColorBrush Danger = new((Color)ColorConverter.ConvertFromString("#D96A6A"));
        public static readonly SolidColorBrush Info = new((Color)ColorConverter.ConvertFromString("#6C9BD1"));

        static Palette()
        {
            Dim.Freeze(); Text.Freeze(); Ok.Freeze(); Warn.Freeze(); Danger.Freeze(); Info.Freeze();
        }
    }

    /// <summary>Mod 状态 → 颜色。</summary>
    public sealed class StatusBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value as string) switch
            {
                "update" => Palette.Ok,
                "latest" => Palette.Dim,
                "error" => Palette.Danger,
                "manual" => Palette.Warn,
                "checking" => Palette.Info,
                "disabled" => Palette.Dim,
                "ignored" => Palette.Dim,
                "smapi" => Palette.Dim,

                // 联机清单比对用
                "missing" => Palette.Danger,
                "older" => Palette.Warn,
                "newer" => Palette.Info,
                "extra" => Palette.Dim,

                _ => Palette.Text
            };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>问题级别 → 颜色。</summary>
    public sealed class LevelBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is IssueLevel lvl
                ? lvl switch
                {
                    IssueLevel.Error => Palette.Danger,
                    IssueLevel.Warning => Palette.Warn,
                    _ => Palette.Info
                }
                : Palette.Text;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>空字符串 / null → 折叠。</summary>
    public sealed class EmptyToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>true → 折叠。</summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>bool 取反。</summary>
    public sealed class InverseBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : value;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : value;
    }
}
