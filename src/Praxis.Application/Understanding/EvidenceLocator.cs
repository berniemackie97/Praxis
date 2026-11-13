using System.Text.RegularExpressions;
using Praxis.Domain.Enrichment;

namespace Praxis.Application.Understanding;

/// <summary>
/// Finds a model's verbatim quote in the document, tolerating differences in whitespace and line
/// breaks (and, as a second pass, letter case). A quote that is not in the document is not
/// evidence. Long quotes are matched on their leading words, within the evidence span limit.
/// </summary>
public static class EvidenceLocator
{
    private const int MaximumWords = 40;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The span of <paramref name="quote"/> within <c>text[start..start+length]</c>, in absolute offsets, or null.</summary>
    public static (int Offset, int Length)? Find(string text, int start, int length, string? quote)
    {
        string[] words = (quote ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return null;
        }

        words = words.Take(MaximumWords).ToArray();
        while (words.Length > 1 && words.Sum(word => word.Length + 1) > ProposalLimits.MaximumEvidenceLength)
        {
            words = words[..^1];
        }

        string pattern = string.Join(@"\s+", words.Select(Regex.Escape));
        foreach (RegexOptions options in new[] { RegexOptions.CultureInvariant, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase })
        {
            try
            {
                Match match = new Regex(pattern, options, Timeout).Match(text, start, length);
                if (match.Success && match.Length <= ProposalLimits.MaximumEvidenceLength)
                {
                    return (match.Index, match.Length);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="name"/> occurs in <paramref name="evidence"/>, ignoring case and whitespace runs.</summary>
    public static bool Mentions(string evidence, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string Normalize(string value) => Regex.Replace(value, @"\s+", " ", RegexOptions.None, Timeout).Trim();
        try
        {
            return Normalize(evidence).Contains(Normalize(name), StringComparison.OrdinalIgnoreCase);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
