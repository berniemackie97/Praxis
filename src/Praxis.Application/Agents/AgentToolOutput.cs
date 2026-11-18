using Praxis.Domain.Agents;
using Praxis.Domain.Knowledge;

namespace Praxis.Application.Agents;

/// <summary>
/// A tool's compact JSON result, the evidence it contains, what policy withheld, and paging state.
/// Citations are numbered by the runtime so answers can cite them as [n].
/// </summary>
public sealed record AgentToolOutput(
    string Content, IReadOnlyList<AgentCitation> Citations, IReadOnlyList<WithheldNotice> Withheld, string? NextCursor = null, KnowledgeSnapshot? InspectedEntity = null)
{
    public static AgentToolOutput Of(object value, IReadOnlyList<AgentCitation>? citations = null, IReadOnlyList<WithheldNotice>? withheld = null,
        string? cursor = null) => new(Tools.ToolJson.Serialize(value), citations ?? [], withheld ?? [], cursor);

    public static AgentToolOutput Error(string code, string message) => Of(new { error = code, message });
}
