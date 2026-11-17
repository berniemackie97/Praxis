using System.Text.RegularExpressions;
using Praxis.Application.Search;
using Praxis.Domain.Search;

namespace Praxis.Application.Agents.Runtime;

/// <summary>Bounded local evidence selection. Reasoning effort never broadens disclosure or expands the automatic brief.</summary>
internal static partial class AgentBrainContext
{
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "answer", "brain", "chat", "could", "does", "exactly", "from", "give", "good", "have", "hello", "help", "know",
        "library", "morning", "afternoon", "evening", "notes", "only", "please", "reply", "respond", "should", "summarize",
        "tell", "thank", "thanks", "that", "their", "this", "want", "what", "where", "which", "with", "would", "your"
    };

    internal static (string Outcome, int Considered, int Withheld, SearchHit[] Selected) Recall(
        ITextSearchIndex index, string prompt, Func<Guid, bool> allowed, int maximum)
    {
        if (DirectReply().IsMatch(prompt) || Greeting().IsMatch(prompt) ||
            (GeneralQuestion().IsMatch(prompt) && !OwnerScope().IsMatch(prompt)))
        {
            return ("not_needed", 0, 0, []);
        }

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
        var ranked = current.Select(hit => new
        {
            Hit = hit,
            Matches = Overlap(terms, hit.Title + " " + hit.Snippet),
            Body = Overlap(terms, hit.Snippet)
        }).Where(hit => !relaxed || hit.Matches >= 2)
            .OrderByDescending(hit => hit.Body)
            .ThenByDescending(hit => hit.Matches).ToArray();
        SearchHit[] relevant = ranked.Select(hit => hit.Hit).ToArray();
        SearchHit[] eligible = relevant.Where(hit => allowed(hit.SourceId)).ToArray();
        SearchHit[] selected = eligible.Take(maximum).ToArray();
        int withheld = relevant.Length - eligible.Length;
        string outcome = selected.Length > 0 ? (relaxed ? "focused_match" : "full_match")
            : withheld > 0 ? "withheld" : current.Length > 0 ? "weak_matches" : "no_matches";
        return (outcome, current.Length, withheld, selected);
    }

    private static int Overlap(string[] terms, string text)
    {
        HashSet<string> evidenceTerms = Terms().Matches(text).Select(match => match.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return terms.Count(evidenceTerms.Contains);
    }

    [GeneratedRegex("""^\s*(?:(?:please|just)\s+)?(?:(?:only|exactly)\s+)?(?:reply|respond|say|print|output)\s+(?:(?:only|exactly|just)\s+)?(?:with\s+)?(?:(?:only|exactly|just)\s+)?(?:[\p{L}\p{N}_-]{1,40}|"[^"\r\n]{1,80}"|'[^'\r\n]{1,80}')(?:\s+(?:only|exactly))?[.!]?\s*$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DirectReply();

    [GeneratedRegex(@"^\s*(?:hi|hello|hey|thanks|thank you|good morning|good afternoon|good evening)[!.\s]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Greeting();

    [GeneratedRegex(@"^\s*(?:(?:please|can you|could you)\s+)?(?:what\s+(?:is|are|was|were)|who\s+(?:is|are|was|were)|where\s+(?:is|are|was|were)|when\s+(?:was|were|did)|why\s+(?:does|do|did|is|are)|how\s+(?:does|do|did)|tell me about|define|explain|describe)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeneralQuestion();

    [GeneratedRegex(@"\b(?:my|mine|our|ours|we|us|me|uploaded|attached|saved|previously|remembered)\b|\b(?:in|from|under|within|according to)\s+(?:the|this|your)\s+(?:(?:\w+\s+){0,2})(?:notes|file|document|docs|repository|repo|codebase|project|library|brain|memory)\b|\b(?:your|my|our)\s+(?:notes|memory|library|sources|files|projects|code)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OwnerScope();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_-]{3,23}")]
    private static partial Regex Terms();
}
