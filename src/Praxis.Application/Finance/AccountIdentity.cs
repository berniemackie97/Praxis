using System.Text.RegularExpressions;
using Praxis.Domain.Finance;

namespace Praxis.Application.Finance;

/// <summary>
/// Derives the cross-document account key from the last four account digits, which every
/// format exposes in some form (OFX ACCTID, masked statement numbers, export file names). Full
/// account numbers are reduced to their mask here and never leave the parser.
/// </summary>
public static partial class AccountIdentity
{
    public static string KeyFor(string lastFour) => "acct-" + lastFour;

    /// <summary>The last four digits of an account identifier, or null when it has fewer than four.</summary>
    public static string? LastFour(string? accountNumber)
    {
        if (accountNumber is null)
        {
            return null;
        }

        string digits = new(accountNumber.Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 4 ? digits[^4..] : null;
    }

    /// <summary>Last four digits printed in a masked form such as "ending in 1234" or "XXXX-1234".</summary>
    public static string? FromText(string text)
    {
        Match match = MaskedNumber().Match(text);
        return match.Success ? match.Groups["digits"].Value : null;
    }

    /// <summary>
    /// A four-digit group in an export file name (for example <c>Chase1234_Activity_20261103.CSV</c>)
    /// that is not part of a date.
    /// </summary>
    public static string? FromFileName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string? masked = FromText(stem);
        if (masked is not null)
        {
            return masked;
        }

        foreach (Match match in DigitRun().Matches(stem))
        {
            string run = match.Value;
            if (run.Length == 4 && !(run.StartsWith("19", StringComparison.Ordinal) || run.StartsWith("20", StringComparison.Ordinal)))
            {
                return run;
            }
        }

        return null;
    }

    public static FinancialAccount Account(string? lastFour, string fallbackKey, string kind, string? institution, string currency, string? label = null)
    {
        string key = lastFour is null ? fallbackKey : KeyFor(lastFour);
        string name = label ?? (lastFour is null ? "Unidentified account" : $"Account ending {lastFour}");
        return new FinancialAccount(key, kind, lastFour is null ? null : "…" + lastFour, institution, currency, name);
    }

    [GeneratedRegex(
        @"(?:ending(?:\s+in)?|acc(?:oun)?t(?:\s*(?:no\.?|number|#))?\s*:?|card(?:\s*(?:no\.?|number|#))?\s*:?)\s*(?:[x*•.#-]+\s*)*(?:\d[\d\s-]*)?(?<digits>\d{4})\b|(?:[x*•]{2,}[\s-]*)+(?<digits>\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaskedNumber();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex DigitRun();
}
