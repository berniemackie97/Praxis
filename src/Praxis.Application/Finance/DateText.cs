using System.Globalization;
using System.Text.RegularExpressions;

namespace Praxis.Application.Finance;

/// <summary>
/// Parses the date spellings banks print: numeric dates in a known field order (with or
/// without year), ISO and compact dates, month names, and QIF's <c>11/ 3'26</c>. Dates carry no
/// time zone; the institution's calendar date is kept as stated.
/// </summary>
public static partial class DateText
{
    private static readonly string[] MonthNames =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>A date token at the start of a statement line.</summary>
    public const string LeadingDatePattern =
        @"^(?<date>\d{4}-\d{1,2}-\d{1,2}|\d{1,2}[/.-]\d{1,2}(?:[/.-]\d{2,4})?|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2}(?:,?\s+\d{4})?|\d{1,2}\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[a-z]*\.?(?:\s+\d{4})?)(?=\s)";

    public static bool TryParse(string? text, DateOrder order, int? defaultYear, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40)
        {
            return false;
        }

        string token = text.Trim().Replace('\'', '/').Replace(" ", "", StringComparison.Ordinal);
        if (token.Length >= 8 && token[..8].All(char.IsAsciiDigit) && (token.Length == 8 || !char.IsAsciiDigit(token[8])))
        {
            return DateOnly.TryParseExact(token[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        if (Numeric().Match(token) is { Success: true } numeric)
        {
            return TryNumeric(numeric, order, defaultYear, out date);
        }

        return TryNamed(text.Trim(), defaultYear, out date);
    }

    /// <summary>The orders under which <paramref name="text"/> is a valid date.</summary>
    public static IEnumerable<DateOrder> PossibleOrders(string text, int? defaultYear = 2000) =>
        Enum.GetValues<DateOrder>().Where(order => TryParse(text, order, defaultYear, out _));

    private static bool TryNumeric(Match match, DateOrder order, int? defaultYear, out DateOnly date)
    {
        date = default;
        int a = int.Parse(match.Groups["a"].Value, CultureInfo.InvariantCulture);
        int b = int.Parse(match.Groups["b"].Value, CultureInfo.InvariantCulture);
        string third = match.Groups["c"].Value;
        bool yearLeading = match.Groups["a"].Value.Length == 4;
        int year;
        int month;
        int day;
        if (yearLeading)
        {
            if (third.Length == 0)
            {
                return false;
            }

            (year, month, day) = (a, b, int.Parse(third, CultureInfo.InvariantCulture));
        }
        else
        {
            if (order == DateOrder.YearFirst)
            {
                return false;
            }

            (month, day) = order == DateOrder.MonthFirst ? (a, b) : (b, a);
            if (third.Length == 0)
            {
                if (defaultYear is not { } fallback)
                {
                    return false;
                }

                year = fallback;
            }
            else
            {
                year = ExpandYear(int.Parse(third, CultureInfo.InvariantCulture), third.Length);
            }
        }

        // An ISO-shaped (year-leading) date is unambiguous, so it is accepted under any order.
        return TryCreate(year, month, day, out date);
    }

    private static bool TryNamed(string text, int? defaultYear, out DateOnly date)
    {
        date = default;
        Match match = Named().Match(text);
        if (!match.Success)
        {
            return false;
        }

        string monthText = (match.Groups["m1"].Success ? match.Groups["m1"] : match.Groups["m2"]).Value;
        int month = Array.FindIndex(MonthNames, name => monthText.StartsWith(name, StringComparison.OrdinalIgnoreCase)) + 1;
        string dayText = (match.Groups["d1"].Success ? match.Groups["d1"] : match.Groups["d2"]).Value;
        string yearText = (match.Groups["y1"].Success ? match.Groups["y1"] : match.Groups["y2"]).Value;
        int year;
        if (yearText.Length == 0)
        {
            if (defaultYear is not { } fallback)
            {
                return false;
            }

            year = fallback;
        }
        else
        {
            year = int.Parse(yearText, CultureInfo.InvariantCulture);
        }

        return month > 0 && TryCreate(year, month, int.Parse(dayText, CultureInfo.InvariantCulture), out date);
    }

    private static int ExpandYear(int year, int digits) => digits switch
    {
        2 => year < 70 ? 2000 + year : 1900 + year,
        4 => year,
        _ => -1
    };

    private static bool TryCreate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1900 or > 2200 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    [GeneratedRegex(@"^(?<a>\d{1,4})[/.-](?<b>\d{1,2})(?:[/.-](?<c>\d{2}|\d{4}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Numeric();

    [GeneratedRegex(
        @"^(?:(?<m1>[A-Za-z]{3,9})\.?\s+(?<d1>\d{1,2})(?:st|nd|rd|th)?(?:,?\s+(?<y1>\d{4}))?|(?<d2>\d{1,2})\s+(?<m2>[A-Za-z]{3,9})\.?(?:,?\s+(?<y2>\d{4}))?)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Named();
}
