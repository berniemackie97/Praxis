using System.Globalization;
using System.Text.RegularExpressions;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Runtime;

/// <summary>
/// Checks a final answer against what the tools actually returned: every substantive figure must
/// appear in a tool result (directly, or as a minor-unit amount shown in major units), and when
/// evidence was available the answer must cite it. Years, small counts and figures the owner
/// typed are exempt.
/// </summary>
public static partial class AnswerVerifier
{
    public static AnswerCheck Check(string answer, IEnumerable<string> toolResults, string userText, bool evidenceAvailable, bool repaired)
    {
        HashSet<decimal> supported = [];
        foreach (string result in toolResults.Append(userText))
        {
            foreach (decimal value in Figures(result))
            {
                supported.Add(value);
                supported.Add(Math.Round(value / 100m, 2));
                supported.Add(Math.Abs(value));
                supported.Add(Math.Round(Math.Abs(value) / 100m, 2));
            }
        }

        string[] unsupported = FigureTokens().Matches(answer).Select(match => match.Value)
            .Where(token => Parse(token) is { } value && IsSubstantive(value, token) && !supported.Contains(Math.Abs(value)) &&
                            !supported.Contains(Math.Round(Math.Abs(value), 2)))
            .Distinct(StringComparer.Ordinal).Take(20).ToArray();
        bool missingCitations = evidenceAvailable && !CitationMarker().IsMatch(answer) && FigureTokens().IsMatch(answer);
        return new AnswerCheck(unsupported, missingCitations, repaired);
    }

    public static string RepairInstruction(AnswerCheck check) =>
        "Answer check failed. " +
        (check.UnsupportedFigures.Count > 0
            ? $"These figures do not appear in any tool result: {string.Join(", ", check.UnsupportedFigures)}. Recompute them with data_query or compute_evaluate, or remove them. "
            : "") +
        (check.MissingCitations ? "Cite the evidence for each claim with its [n] number. " : "") +
        "Then give your corrected final answer.";

    private static IEnumerable<decimal> Figures(string text) =>
        FigureTokens().Matches(text).Select(match => Parse(match.Value)).OfType<decimal>();

    private static decimal? Parse(string token)
    {
        string cleaned = token.Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).TrimEnd('%').Trim();
        return decimal.TryParse(cleaned, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value) ? value : null;
    }

    private static bool IsSubstantive(decimal value, string token)
    {
        decimal magnitude = Math.Abs(value);
        bool year = decimal.Truncate(magnitude) == magnitude && magnitude is >= 1900 and <= 2100 && !token.Contains('$') && !token.Contains('.');
        return !year && (magnitude >= 32 || token.Contains('.') || token.Contains('$') || token.EndsWith('%'));
    }

    [GeneratedRegex(@"(?<![\w\[/-])-?\$?\d{1,3}(?:,\d{3})+(?:\.\d+)?%?|(?<![\w\[/-])-?\$?\d+(?:\.\d+)?%?(?![\w\]/-])", RegexOptions.CultureInvariant)]
    private static partial Regex FigureTokens();

    [GeneratedRegex(@"\[\d{1,3}\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationMarker();
}
