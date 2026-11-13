using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Data;

/// <summary>A dataset row: typed values by field name, plus the evidence that supports it.</summary>
public sealed record DataRow(IReadOnlyDictionary<string, object?> Values, IReadOnlyList<AgentCitation> Evidence);
