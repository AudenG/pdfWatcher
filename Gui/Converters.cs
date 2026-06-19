using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PdfWatcher.Gui;

[ValueConversion(typeof(bool), typeof(string))]
public class BoolToCheckConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => (bool)value ? "✓" : "✗";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(System.Windows.Media.Brush))]
public class BoolToBrushConverter : IValueConverter
{
    private static readonly System.Windows.Media.Brush GreenBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x26, 0xB5, 0x66));
    private static readonly System.Windows.Media.Brush RedBrush   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0x2B, 0x2B));

    public object Convert(object value, Type t, object p, CultureInfo c) => (bool)value ? GreenBrush : RedBrush;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

[ValueConversion(typeof(int), typeof(Visibility))]
public class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (int)value == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (bool)value ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Visibility))]
public class BoolToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (bool)value ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
