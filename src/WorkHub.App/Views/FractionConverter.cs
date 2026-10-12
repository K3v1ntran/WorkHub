using System.Globalization;
using System.Windows.Data;

namespace WorkHub.App.Views;

/// <summary>Length → length × <see cref="Fraction"/> (e.g. the Note preview may use at most 40 % of the content area).</summary>
public sealed class FractionConverter : IValueConverter
{
    public double Fraction { get; set; } = 0.5;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double length && length > 0 ? length * Fraction : double.PositiveInfinity;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
