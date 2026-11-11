using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TypographyFontFileViewer.Controls;

/// <summary>Shows an element only when the bound value is present and not blank.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            _ => true,
        };

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// True when the bound enum equals the parameter, and sets it back when checked.
/// </summary>
/// <remarks>
/// This is what lets a pair of radio buttons drive a single enum property, which is how
/// the view switch and the gallery's size presets both work.
/// </remarks>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null
        && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Only the button being turned on should write; the one turning off must not.
        if (value is not true || parameter is not string name) return Binding.DoNothing;

        Type enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return enumType.IsEnum && Enum.TryParse(enumType, name, out object? parsed)
            ? parsed
            : Binding.DoNothing;
    }
}

/// <summary>Shows an element only while the bound enum equals the parameter.</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && parameter is not null
        && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// True when a bound number equals the parameter, and sets it back when checked.
/// </summary>
/// <remarks>
/// The gallery's size presets are points on the same scale the slider moves along, so
/// picking Large and dragging the slider to 44 are the same thing, and the preset
/// highlights itself when the slider happens to land on it.
/// </remarks>
public sealed class DoubleEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double number
        && parameter is string text
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double target)
        && Math.Abs(number - target) < 0.5;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is not string text) return Binding.DoNothing;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double target)
            ? target
            : Binding.DoNothing;
    }
}

/// <summary>
/// Maps an enum to the index of the tab that shows it, and back again.
/// </summary>
/// <remarks>
/// The panel strip inside the single-font view will not have all its tabs until Phase 6,
/// so an index past the end of the strip reads back as the first panel rather than as a
/// selection that does not exist.
/// </remarks>
public sealed class EnumToIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? 0 : System.Convert.ToInt32(value, culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        Type enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (!enumType.IsEnum) return Binding.DoNothing;

        int index = value is null ? 0 : System.Convert.ToInt32(value, culture);
        if (index < 0) index = 0;

        return Enum.IsDefined(enumType, index)
            ? Enum.ToObject(enumType, index)
            : Enum.ToObject(enumType, 0);
    }
}

/// <summary>Shows an element only when the bound flag is false.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Shows an element only when a collection is empty, for "nothing here" messages.</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int count = value switch
        {
            null => 0,
            int number => number,
            System.Collections.ICollection collection => collection.Count,
            _ => 1,
        };

        return count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
