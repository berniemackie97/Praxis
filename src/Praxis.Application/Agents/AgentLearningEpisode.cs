using Praxis.Domain.Agents;

namespace Praxis.Application.Agents;

/// <summary>References retained first-party work; generated narratives are not verification receipts.</summary>
public sealed record AgentLearningEpisode(Guid RunId, Guid ConversationId, string Request, AgentReply Reply, IReadOnlyList<AgentMessage> Messages,
    IReadOnlySet<(string CallId, string Tool)> AuditedCalls);
