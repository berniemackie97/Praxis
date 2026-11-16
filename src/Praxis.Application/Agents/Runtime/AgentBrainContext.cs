using System.Text.RegularExpressions;
using Praxis.Application.Search;
using Praxis.Domain.Search;

namespace Praxis.Application.Agents.Runtime;

/// <summary>Bounded local evidence selection. Reasoning effort never broadens disclosure or expands the automatic brief.</summary>
internal static partial class AgentBrainContext
{
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "answer", "brain", "chat", "could", "does", "exactly", "from", "give", "have", "help", "know", "library",
        "notes", "only", "please", "reply", "respond", "should", "summarize", "tell", "that", "their", "this", "want",
        "what", "where", "which", "with", "would", "your"
    };

    internal static (string Outcome, int Considered, int Withheld, SearchHit[] Selected) Recall(
        ITextSearchIndex index, string prompt, Func<Guid, bool> allowed, int maximum)
    {
        string[] terms = Terms().Matches(prompt).Select(match => match.Value)
            .Where(term => !CommonWords.Contains(term)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        if (terms.Length == 0)
        {
            return ("no_query", 0, 0, []);
        }

        string query = string.Join(' ', terms);
        IReadOnlyList<SearchHit> hits = index.RetainCurrent(index.QueryPassages(query, 12));
        bool relaxed = hits.Count == 0 && terms.Length > 1;
        if (relaxed)
        {
            hits = index.RetainCurrent(index.QueryPassages(query, 12, anyTerm: true));
        }

        SearchHit[] current = hits.Where(hit => !string.IsNullOrWhiteSpace(hit.Snippet)).DistinctBy(hit => hit.SourceId).ToArray();
        SearchHit[] relevant = relaxed
            ? current.Select(hit => (Hit: hit, Matches: Overlap(terms, hit))).Where(hit => hit.Matches >= 2)
                .OrderByDescending(hit => hit.Matches).Select(hit => hit.Hit).ToArray()
            : current;
        SearchHit[] eligible = relevant.Where(hit => allowed(hit.SourceId)).ToArray();
        SearchHit[] selected = eligible.Take(maximum).ToArray();
        int withheld = relevant.Length - eligible.Length;
        string outcome = selected.Length > 0 ? (relaxed ? "focused_match" : "full_match")
            : withheld > 0 ? "withheld" : current.Length > 0 ? "weak_matches" : "no_matches";
        return (outcome, current.Length, withheld, selected);
    }

    private static int Overlap(string[] terms, SearchHit hit)
    {
        HashSet<string> evidenceTerms = Terms().Matches(hit.Title + " " + hit.Snippet).Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return terms.Count(evidenceTerms.Contains);
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_-]{3,23}")]
    private static partial Regex Terms();
}
