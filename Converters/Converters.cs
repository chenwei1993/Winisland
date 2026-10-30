using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinIsland.Converters;

/// <summary>null → Visible (or Collapsed when <see cref="Invert"/> is set).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null;
        if (Invert)
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true → Visible, false → Collapsed. Invert swaps the behavior.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        return flag ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Scales a 0..100 percentage into a pixel width.</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public double MaxWidth { get; set; } = 12;
    public double MinWidth { get; set; } = 2;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double percent;
        try
        {
            percent = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            percent = 0;
        }

        return Math.Max(MinWidth, Math.Clamp(percent, 0, 100) / 100d * MaxWidth);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
