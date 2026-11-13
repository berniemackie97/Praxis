using Praxis.Domain.Agents;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Owner memories learned in conversation.</summary>
public sealed record AgentMemoriesResponse(IReadOnlyList<AgentMemory> Memories);
