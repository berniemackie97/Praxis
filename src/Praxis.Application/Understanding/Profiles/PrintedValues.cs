using System.Globalization;
using System.Text.RegularExpressions;
using Praxis.Application.Finance;

namespace Praxis.Application.Understanding.Profiles;

/// <summary>
/// Checks that a value a model states is printed in its evidence: a calendar date in any common
/// spelling (with the date's own year when the text omits it), a time of day, or an amount.
/// This is what stops a model from inventing a date or a figure.
/// </summary>
public static partial class PrintedValues
{
    public static bool HasDate(string evidence, DateOnly date)
    {
        foreach (Match match in DateCandidates().Matches(evidence))
        {
            foreach (DateOrder order in Enum.GetValues<DateOrder>())
            {
                if (DateText.TryParse(match.Value, order, date.Year, out DateOnly parsed) && parsed == date)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool HasTime(string evidence, TimeOnly time)
    {
        foreach (Match match in TimeCandidates().Matches(evidence))
        {
            int hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
            int minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
            string meridiem = match.Groups["meridiem"].Value.ToLowerInvariant();
            if (meridiem.StartsWith('p') && hour < 12)
            {
                hour += 12;
            }
            else if (meridiem.StartsWith('a') && hour == 12)
            {
                hour = 0;
            }

            if ((match.Groups["minute"].Success || meridiem.Length > 0) && hour == time.Hour && minute == time.Minute)
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasAmount(string evidence, decimal amount)
    {
        foreach (Match match in AmountCandidates().Matches(evidence))
        {
            if (MoneyText.TryParse(match.Value, out decimal value, out _) && Math.Abs(value) == Math.Abs(amount))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\d{4}-\d{1,2}-\d{1,2}|\d{1,2}[/.-]\d{1,2}(?:[/.-]\d{2,4})?|(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?\s+\d{1,2}(?:st|nd|rd|th)?(?:,?\s+\d{4})?|\d{1,2}(?:st|nd|rd|th)?\s+(?:of\s+)?(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?(?:,?\s+\d{4})?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex DateCandidates();

    [GeneratedRegex(@"\b(?<hour>[01]?\d|2[0-3])(?::(?<minute>[0-5]\d))?\s*(?<meridiem>[ap]\.?m\.?)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 250)]
    private static partial Regex TimeCandidates();

    [GeneratedRegex(@"[-+(]?\s*[$€£¥₹]?\s*\d[\d,.' ]*\d(?:\.\d+)?\)?|[-+(]?\s*[$€£¥₹]?\s*\d", RegexOptions.CultureInvariant, 250)]
    private static partial Regex AmountCandidates();
}
