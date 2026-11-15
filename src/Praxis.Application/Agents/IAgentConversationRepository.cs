using Praxis.Domain.Agents;

namespace Praxis.Application.Agents;

/// <summary>
/// Owner-private agent state: backend profiles, conversations and messages, the tool-call audit,
/// proposals, memories and authored artifacts.
/// </summary>
public interface IAgentConversationRepository
{
    IReadOnlyList<AgentBackendProfile> ReadBackends();

    void SaveBackend(AgentBackendProfile profile);

    AgentConversation Create(string backendId, string title);

    AgentConversation? Read(Guid conversationId);

    bool Rename(Guid conversationId, string title);

    bool Delete(Guid conversationId);

    IReadOnlyList<AgentConversation> List(int limit);

    IReadOnlyList<AgentMessage> ReadMessages(Guid conversationId);

    void Append(Guid conversationId, AgentMessage message);

    void RecordToolCall(AgentToolCallRecord record);

    IReadOnlyList<AgentToolCallRecord> ReadToolCalls(Guid conversationId, int limit);

    AgentProposal AddProposal(Guid? conversationId, string kind, string payloadJson, string rationale, string status);

    IReadOnlyList<AgentProposal> ReadProposals(string? status, int limit);

    AgentProposal? ReadProposal(Guid proposalId);

    bool SetProposalStatus(Guid proposalId, string expectedStatus, string status);

    AgentMemory AddMemory(Guid? conversationId, string text, string status);

    IReadOnlyList<AgentMemory> ReadMemories(string? status, string? search, int limit);

    bool SetMemoryStatus(Guid memoryId, string status);

    void SaveArtifact(AgentArtifact artifact);

    AgentArtifact? ReadArtifact(Guid artifactId);

    IReadOnlyList<AgentArtifact> ReadArtifacts(int limit);
}
