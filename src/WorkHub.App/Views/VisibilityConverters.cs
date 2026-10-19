using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WorkHub.App.Views;

/// <summary>null (or an empty string) → Collapsed; anything else → Visible.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>true → Collapsed; false → Visible (the tree hides while search results are shown).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Phase 13A: a width (double) → true when it is greater than 0 and less than <see cref="Threshold"/>.</summary>
public sealed class LessThanConverter : IValueConverter
{
    public double Threshold { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double width && width > 0 && width < Threshold;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
