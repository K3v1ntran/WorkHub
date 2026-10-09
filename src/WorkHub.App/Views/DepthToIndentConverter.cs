using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WorkHub.App.Views;

/// <summary>
/// Tree depth → left margin. Lets a tree row's background span the full width while its content is indented.
/// </summary>
public sealed class DepthToIndentConverter : IValueConverter
{
    public double Indent { get; set; } = 18;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(value is int depth && depth > 0 ? depth * Indent : 0, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
