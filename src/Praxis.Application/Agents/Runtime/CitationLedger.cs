using Praxis.Application.Agents.Tools;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Runtime;

/// <summary>
/// Numbers every piece of evidence returned during a conversation so answers can cite [n], and
/// wraps each tool result with its citation numbers and withheld notices as the model sees it.
/// </summary>
public sealed class CitationLedger
{
    private readonly List<AgentCitation> citations = [];
    private readonly Dictionary<(Guid, int, string), int> numbers = [];

    public IReadOnlyList<AgentCitation> All => citations;

    public string Wrap(AgentToolOutput output)
    {
        var cited = output.Citations.Select(citation => new { n = Number(citation), source = citation.SourceName, locator = citation.Locator }).ToArray();
        var withheld = output.Withheld.Select(notice => new { notice.Count, notice.Reason }).ToArray();
        return $$"""{"result":{{output.Content}},"citations":{{ToolJson.Serialize(cited)}}{{(withheld.Length == 0 ? "" : ",\"withheld\":" + ToolJson.Serialize(withheld))}}{{(output.NextCursor is null ? "" : ",\"nextCursor\":" + ToolJson.Serialize(output.NextCursor))}}}""";
    }

    private int Number(AgentCitation citation)
    {
        (Guid, int, string) key = (citation.ItemId, citation.Revision, citation.Locator);
        if (!numbers.TryGetValue(key, out int number))
        {
            citations.Add(citation);
            number = citations.Count;
            numbers[key] = number;
        }

        return number;
    }
}
