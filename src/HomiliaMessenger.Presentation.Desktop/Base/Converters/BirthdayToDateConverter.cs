using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace HomiliaMessenger.Presentation.Desktop.Base.Converters;

/// <summary>
/// Converts a contact's <see cref="DateOnly"/> birthday to and from the <see cref="DateTimeOffset"/> used by <c>DatePicker</c>.
/// Only the calendar date is used: the picker's time and offset are discarded so the date never shifts.
/// </summary>
public class BirthdayToDateConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is DateOnly date
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            DateTimeOffset offset => DateOnly.FromDateTime(offset.DateTime),
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => null
        };
    }
}
