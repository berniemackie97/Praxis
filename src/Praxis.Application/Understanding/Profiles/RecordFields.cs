using System.Globalization;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding.Profiles;

/// <summary>Shared parsing and bounds for record fields.</summary>
public static class RecordFields
{
    public static string? Text(IReadOnlyDictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out string? value) && value.Trim() is { Length: > 0 and <= IntakeRecord.MaximumFieldLength } text && !text.Any(char.IsControl)
            ? text
            : null;

    /// <summary>Parses <c>yyyy-MM-dd</c> or <c>yyyy-MM-ddTHH:mm</c> into a date and optional time.</summary>
    public static (DateOnly Date, TimeOnly? Time)? Moment(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            return (date, null);
        }

        return DateTime.TryParseExact(value, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime moment)
            ? (DateOnly.FromDateTime(moment), TimeOnly.FromDateTime(moment))
            : null;
    }

    /// <summary>
    /// The verified moment as stored: the date when it is printed in the evidence, with the time
    /// only when that is printed too. Null when the date is not printed.
    /// </summary>
    public static string? VerifiedMoment(string? value, string evidence)
    {
        if (Moment(value) is not { } moment || !PrintedValues.HasDate(evidence, moment.Date))
        {
            return null;
        }

        string day = moment.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return moment.Time is { } time && PrintedValues.HasTime(evidence, time) ? $"{day}T{time:HH\\:mm}" : day;
    }
}
