using System.Globalization;
using System.Text.RegularExpressions;

namespace Praxis.Application.Finance;

/// <summary>
/// Exact parsing of printed money: currency symbols or codes, grouping separators, decimal
/// comma or point, leading or trailing minus, parentheses and CR/DR suffixes. Values are
/// decimal; nothing is parsed through floating point.
/// </summary>
public static partial class MoneyText
{
    /// <summary>
    /// A printed amount with exactly two decimals, as statements print them. Requiring the
    /// decimals keeps reference numbers, dates and counts from being read as amounts.
    /// </summary>
    public const string StatementAmountPattern =
        @"(?<![\w.,/])\(?[-+]?\s?(?:[$€£¥]\s?)?-?(?:\d{1,3}(?:,\d{3})+|\d+)\.\d{2}\)?(?:\s?(?:CR|DR|-)(?![\w]))?(?![\w.,/])";

    public static bool TryParse(string? text, out decimal value, out MoneyMarker marker)
    {
        value = 0;
        marker = MoneyMarker.None;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64)
        {
            return false;
        }

        string token = text.Trim();
        bool negative = false;
        if (TrailingMarker().Match(token) is { Success: true } suffix)
        {
            marker = suffix.Groups[1].Value.Equals("CR", StringComparison.OrdinalIgnoreCase) ? MoneyMarker.Credit : MoneyMarker.Debit;
            token = token[..suffix.Index].TrimEnd();
        }

        if (token.EndsWith('-'))
        {
            negative = true;
            token = token[..^1].TrimEnd();
        }

        if (token.StartsWith('(') && token.EndsWith(')'))
        {
            negative = !negative;
            token = token[1..^1].Trim();
        }

        token = CurrencyAffix().Replace(token, "").Trim();
        if (token.StartsWith('-') || token.StartsWith('+'))
        {
            negative ^= token[0] == '-';
            token = token[1..].TrimStart();
            token = CurrencyAffix().Replace(token, "").Trim();
        }

        if (!TryParseNumber(token, out decimal magnitude))
        {
            return false;
        }

        value = negative ? -magnitude : magnitude;
        return true;
    }

    private static bool TryParseNumber(string token, out decimal value)
    {
        value = 0;
        if (token.Length == 0 || !Number().IsMatch(token))
        {
            return false;
        }

        int lastComma = token.LastIndexOf(',');
        int lastPoint = token.LastIndexOf('.');
        string normalized;
        if (lastComma > lastPoint && token.Length - lastComma - 1 is 1 or 2 or 3 && (lastPoint >= 0 || token.Length - lastComma - 1 != 3))
        {
            // Decimal comma: 1.234,56 or 12,5. A lone comma followed by three digits is grouping.
            normalized = token.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        }
        else
        {
            normalized = token.Replace(",", "", StringComparison.Ordinal);
        }

        if (normalized.Count(c => c == '.') > 1)
        {
            return false;
        }

        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    [GeneratedRegex(@"\s?(CR|DR)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrailingMarker();

    [GeneratedRegex(@"^(?:[A-Z]{3}\s?|[$€£¥])|(?:\s?[A-Z]{3}|[$€£¥])$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyAffix();

    [GeneratedRegex(@"^\d[\d,.]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Number();
}
