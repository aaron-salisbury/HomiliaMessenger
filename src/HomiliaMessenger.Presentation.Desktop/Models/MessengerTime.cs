using System;
using System.Globalization;

namespace HomiliaMessenger.Presentation.Desktop.Models;

/// <summary>
/// Messenger-style timestamp text: compact for recent times, fuller as they get older. Times are shown in local time.
/// </summary>
internal static class MessengerTime
{
    /// <summary>
    /// For the conversation list: a time today, a weekday within the past week, otherwise a date.
    /// </summary>
    internal static string ForList(DateTimeOffset timestamp, DateTimeOffset now)
    {
        DateTime local = timestamp.ToLocalTime().DateTime;
        DateTime today = now.ToLocalTime().Date;

        if (local.Date == today)
        {
            return local.ToString("t", CultureInfo.CurrentCulture);
        }

        if (local.Date > today.AddDays(-7))
        {
            return local.ToString("ddd", CultureInfo.CurrentCulture);
        }

        return local.Year == today.Year
            ? local.ToString("MMM d", CultureInfo.CurrentCulture)
            : local.ToString("d", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// For the separators between bursts of messages in a thread.
    /// </summary>
    internal static string ForSeparator(DateTimeOffset timestamp, DateTimeOffset now)
    {
        DateTime local = timestamp.ToLocalTime().DateTime;
        DateTime today = now.ToLocalTime().Date;
        string time = local.ToString("t", CultureInfo.CurrentCulture);

        if (local.Date == today)
        {
            return time;
        }

        if (local.Date == today.AddDays(-1))
        {
            return $"Yesterday {time}";
        }

        if (local.Date > today.AddDays(-7))
        {
            return $"{local.ToString("dddd", CultureInfo.CurrentCulture)} {time}";
        }

        string date = local.Year == today.Year
            ? local.ToString("MMM d", CultureInfo.CurrentCulture)
            : local.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);

        return $"{date}, {time}";
    }

    /// <summary>
    /// The full local date and time, for tooltips.
    /// </summary>
    internal static string ForToolTip(DateTimeOffset timestamp)
    {
        return timestamp.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
    }
}
