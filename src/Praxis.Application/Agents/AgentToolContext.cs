using Praxis.Domain.Agents;

namespace Praxis.Application.Agents;

/// <summary>
/// The session a tool runs for: conversation, backend identity and locality, the owner's tool
/// permission, and the disclosure predicate. Tools must drop any source for which
/// <see cref="MayDisclose"/> is false before building output, and report it as withheld.
/// </summary>
public sealed record AgentToolContext(Guid ConversationId, string BackendId, bool BackendIsLocal, string ToolPermission, Func<Guid, bool> MayDisclose,
    string? OwnerText = null, string ContextPreset = AgentContextPreset.Expanded);
