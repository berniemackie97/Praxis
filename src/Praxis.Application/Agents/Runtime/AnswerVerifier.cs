using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Runtime;

/// <summary>
/// Checks substantive decimal and hexadecimal figures against available tool evidence.
/// When a source was opened, search-result previews alone cannot support those figures or citations.
/// Year-like labels, small counts and figures the owner typed are exempt from decimal checks.
/// </summary>
public static partial class AnswerVerifier
{
    public static AnswerCheck Check(string answer, IEnumerable<string> toolResults, string userText, bool evidenceAvailable, bool repaired,
        int availableCitations = 0, IReadOnlySet<int>? openedCitations = null,
        IReadOnlyDictionary<int, string>? citedEvidence = null, IReadOnlySet<int>? searchPreviews = null)
    {
        string[] evidence = toolResults.Select(VerifiableEvidence).Append(userText).ToArray();
        HashSet<decimal> supported = [];
        foreach (string result in evidence)
        {
            foreach (decimal value in Figures(result))
            {
                supported.Add(value);
                supported.Add(Math.Round(value / 100m, 2));
                supported.Add(Math.Abs(value));
                supported.Add(Math.Round(Math.Abs(value) / 100m, 2));
            }
        }

        HashSet<string> evidenceHex = evidence
            .SelectMany(value => HexTokens().Matches(value).Select(match => match.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] unsupported = FigureTokens().Matches(answer).Select(match => match.Value)
            .Where(token => Parse(token) is { } value && IsSubstantive(value, token) && !supported.Contains(Math.Abs(value)) &&
                            !supported.Contains(Math.Round(Math.Abs(value), 2)))
            .Concat(HexTokens().Matches(answer).Select(match => match.Value).Where(token => !evidenceHex.Contains(token)))
            .Concat(UnsupportedCitedFigures(answer, citedEvidence, openedCitations))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        int[] citations = CitationMarker().Matches(answer)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        bool missingCitations = evidenceAvailable &&
            (citations.Length == 0 ||
             citations.Any(number => number <= 0 || availableCitations > 0 && number > availableCitations) ||
             openedCitations is { Count: > 0 } &&
                 (!citations.Any(openedCitations.Contains) ||
                  searchPreviews is not null && citations.Any(searchPreviews.Contains)));
        return new AnswerCheck(unsupported, missingCitations, repaired);
    }

    public static string RepairInstruction(AnswerCheck check) =>
        "Answer check failed. " +
        (check.UnsupportedFigures.Count > 0
            ? $"These figures are not verified by the supporting evidence: {string.Join(", ", check.UnsupportedFigures)}. Recompute them with data_query or compute_evaluate, or remove them. "
            : "") +
        (check.MissingCitations ? "Cite the evidence actually read with its valid [n] number; a search hit alone does not prove source content. " : "") +
        "Then give your corrected final answer.";

    private static IEnumerable<string> UnsupportedCitedFigures(string answer,
        IReadOnlyDictionary<int, string>? citedEvidence, IReadOnlySet<int>? openedCitations)
    {
        if (citedEvidence is null || citedEvidence.Count == 0)
        {
            yield break;
        }

        foreach (string sentence in SentenceBoundary().Split(answer))
        {
            int[] cited = CitationMarker().Matches(sentence)
                .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
            if (cited.Length == 0)
            {
                continue;
            }

            string[] support = cited
                .Where(number => openedCitations is not { Count: > 0 } || openedCitations.Contains(number))
                .Select(number => citedEvidence.TryGetValue(number, out string? value) ? VerifiableEvidence(value) : null)
                .OfType<string>().ToArray();
            if (support.Length == 0)
            {
                continue;
            }

            HashSet<decimal> numbers = support.SelectMany(Figures).Select(value => Math.Abs(value)).ToHashSet();
            HashSet<string> hex = support.SelectMany(text => HexTokens().Matches(text).Select(match => match.Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in FigureTokens().Matches(sentence))
            {
                if (Parse(match.Value) is { } number && IsSubstantive(number, match.Value) &&
                    !numbers.Contains(Math.Abs(number)))
                {
                    yield return match.Value;
                }
            }

            foreach (Match match in HexTokens().Matches(sentence))
            {
                if (!hex.Contains(match.Value))
                {
                    yield return match.Value;
                }
            }
        }
    }

    private static string VerifiableEvidence(string receipt)
    {
        if (!receipt.TrimStart().StartsWith('{'))
        {
            return receipt;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(receipt);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return receipt;
            }

            JsonElement result = root.TryGetProperty("result", out JsonElement wrapped) &&
                wrapped.ValueKind == JsonValueKind.Object ? wrapped : root;
            if (result.TryGetProperty("sourceId", out _) && result.TryGetProperty("text", out JsonElement text) &&
                text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? "";
            }
        }
        catch (JsonException)
        {
            // Plain evidence is allowed; only structured source receipts require projection.
        }

        return receipt;
    }

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

    [GeneratedRegex(@"(?<=[.!?])\s+(?=\S)|\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"\[(\d{1,3})\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationMarker();

    [GeneratedRegex(@"(?<![\w])0[xX][0-9a-fA-F]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex HexTokens();
}
