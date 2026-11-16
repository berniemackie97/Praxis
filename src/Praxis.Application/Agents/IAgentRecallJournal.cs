using Praxis.Domain.Agents;

namespace Praxis.Application.Agents;

public interface IAgentRecallJournal
{
    void Record(Guid conversationId, AgentRecallReceipt receipt);
    IReadOnlyList<AgentRecallReceipt> Read(Guid conversationId, int limit);
}
