using System;
using System.Globalization;

namespace HomiliaMessenger.Business.Modules.Contacts;

/// <summary>
/// Calendar-date rules for a contact's birthday. Birthdays are dates without a time or offset; the canonical text form is
/// ISO 8601 <c>yyyy-MM-dd</c>.
/// </summary>
public static class ContactBirthday
{
    public const string FORMAT = "yyyy-MM-dd";

    /// <summary>
    /// Parses ISO dates (<c>yyyy-MM-dd</c>, <c>yyyyMMdd</c>) and ISO timestamps, taking the date portion as written.
    /// </summary>
    public static bool TryParse(string? text, out DateOnly date)
    {
        date = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        int timeSeparator = trimmed.IndexOf('T');
        string datePart = timeSeparator > 0 ? trimmed[..timeSeparator] : trimmed;

        return DateOnly.TryParseExact(datePart, [FORMAT, "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    public static string Format(DateOnly date)
    {
        return date.ToString(FORMAT, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns the age in whole years of someone born on <paramref name="birthday"/>, as of <paramref name="today"/>.
    /// A February 29 birthday is counted on February 28 in common years.
    /// </summary>
    public static int AgeOn(DateOnly birthday, DateOnly today)
    {
        int age = today.Year - birthday.Year;

        if (birthday.AddYears(age) > today)
        {
            age--;
        }

        return age;
    }
}
