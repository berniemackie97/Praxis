using System.Text.RegularExpressions;
using Praxis.Application.Search;
using Praxis.Domain.Agents;
using Praxis.Domain.Search;

namespace Praxis.Application.Agents.Runtime;

/// <summary>Cheap local recall from current indexed evidence. No model or embedding call.</summary>
internal static partial class AgentBrainContext
{
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "could", "does", "from", "give", "have", "help", "know", "only", "please",
        "reply", "respond", "should", "summarize", "tell", "that", "their", "this", "what",
        "where", "which", "with", "would", "your"
    };

    internal static AgentToolOutput? Recall(ITextSearchIndex index, string prompt, Func<Guid, bool> allowed)
    {
        string query = string.Join(" ", Terms().Matches(prompt).Select(match => match.Value)
            .Where(term => !CommonWords.Contains(term)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8));
        if (query.Length == 0)
        {
            return null;
        }

        IReadOnlyList<SearchHit> hits = index.QueryPassages(query, 12);
        if (hits.Count == 0 && query.Contains(' '))
        {
            hits = index.QueryPassages(query, 12, anyTerm: true);
        }

        SearchHit[] selected = index.RetainCurrent(hits).Where(hit => allowed(hit.SourceId))
            .DistinctBy(hit => hit.SourceId).Take(4).ToArray();
        if (selected.Length == 0)
        {
            return null;
        }

        return AgentToolOutput.Of(new
        {
            excerpts = selected.Select(hit => new { snippet = hit.Snippet, source = Path.GetFileName(hit.SourceLocator) })
        }, selected.Select(hit => new AgentCitation(hit.SourceId, hit.ItemId, hit.Revision, "search", Path.GetFileName(hit.SourceLocator))).ToArray());
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}_-]{3,23}")]
    private static partial Regex Terms();
}
