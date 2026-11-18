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

    internal static bool IsSourceIndependent(string prompt, string? previousPrompt = null) =>
        DirectReply().IsMatch(prompt) || Greeting().IsMatch(prompt) ||
        SelfContainedComputation(prompt) ||
        GeneralQuestion().IsMatch(prompt) && !OwnerScope().IsMatch(prompt) && !IsOwnerFollowUp(prompt, previousPrompt);

    private static bool SelfContainedComputation(string prompt) =>
        ComputeTool().IsMatch(prompt) && ArithmeticExpression().IsMatch(prompt) &&
        !OwnerScope().IsMatch(prompt) && !SourceReference().IsMatch(prompt);

    private static bool IsOwnerFollowUp(string prompt, string? previousPrompt) =>
        previousPrompt is not null && OwnerScope().IsMatch(previousPrompt) && SourceFollowUp().IsMatch(prompt);

    internal static (string Outcome, int Considered, int Withheld, SearchHit[] Selected) Recall(
        ITextSearchIndex index, string prompt, Func<Guid, bool> allowed, int maximum, string? previousPrompt = null)
    {
        if (IsSourceIndependent(prompt, previousPrompt))
        {
            return ("not_needed", 0, 0, []);
        }

        string[] terms = Terms().Matches(IsOwnerFollowUp(prompt, previousPrompt) ? previousPrompt + " " + prompt : prompt)
            .Select(match => match.Value)
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

    [GeneratedRegex(@"^\s*(?:(?:i\s+(?:wonder|was\s+wondering)|i(?:'m|\s+am)\s+curious)\s*:?\s*(?:what|who|where|when|why|how)\b|(?:(?:please|can you|could you)\s+)?(?:what\s+(?:is|are|was|were)|who\s+(?:is|are|was|were)|where\s+(?:is|are|was|were)|when\s+(?:was|were|did)|why\s+(?:does|do|did|is|are)|how\s+(?:does|do|did)|tell\s+me\s+(?:about|what|who)|define|explain|describe)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeneralQuestion();

    [GeneratedRegex(@"\b(?:my|mine|our|ours|uploaded|attached|saved|previously|remembered)\b|\b(?:in|from|under|within|according to)\s+(?:the|this|your)\s+(?:(?:\w+\s+){0,2})(?:notes|file|document|docs|repository|repo|codebase|project|library|brain|memory)\b|\b(?:your|my|our)\s+(?:notes|memory|library|sources|files|projects|code)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OwnerScope();

    [GeneratedRegex(@"^\s*(?:and\s+(?:what|how|why|when|where|who|does|is|are)\b|what\s+about\b|how\s+about\b|what\s+is\s+(?:its|their)\b|(?:does|is|was)\s+(?:it|that)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceFollowUp();

    [GeneratedRegex(@"\bcompute_evaluate\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComputeTool();

    [GeneratedRegex(@"(?<![\w.])\d+(?:\.\d+)?\s*[-+*/]\s*\d+(?:\.\d+)?(?!\w|\.\d)", RegexOptions.CultureInvariant)]
    private static partial Regex ArithmeticExpression();

    [GeneratedRegex(@"\b(?:source|document|file|notes|library|repository|repo|dataset|attachment|uploaded|linked|website|webpage|url)\b|\b[\w.-]+\.[a-z]{1,8}\b|https?://",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SourceReference();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_-]{3,23}")]
    private static partial Regex Terms();
}
