using Praxis.Application.Agents;
using Praxis.Application.Agents.Memory;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Models;
using Praxis.Domain.Agents;

namespace Praxis.Application.Desktop.Actions;

/// <summary>
/// First-party chat: configure backends, start conversations, send messages (run in the
/// background; poll <c>agentRun</c> for events and the reply), cancel, and review the agent's
/// proposals and memories. Reachable only through the owner's private pipe.
/// </summary>
public sealed class AgentActions(
    IAgentConversationRepository repository, AgentRunCoordinator runs, ProposalApplier proposals, SessionSecretStore secrets, ModelSetup? setup = null,
    IChatMemoryStore? memory = null, IAgentRecallJournal? recalls = null)
    : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("agentBackends", _ =>
        {
            // Every connected model, local or remote, can chat.
            setup?.EnsureChatBackends();
            return new AgentBackendsResponse(repository.ReadBackends());
        }),
        DesktopAction.Of("agentBackendSave", fields =>
        {
            repository.SaveBackend(new AgentBackendProfile(fields.RequireString("backendId", 64), fields.RequireString("kind", 32),
                fields.OptionalText("endpoint", 512, "The endpoint field is invalid."), fields.RequireString("model", 200),
                fields.OptionalText("keyAccount", 128, "The keyAccount field is invalid."),
                fields.OptionalText("executable", 1024, "The executable field is invalid."), fields.RequireBoolean("enabled"),
                fields.OptionalText("effort", 16, "The effort field is invalid.")));
            return new ChangedResponse(true);
        }),
        // Keys arrive from the owner's Keychain through the private pipe and stay in worker memory only.
        DesktopAction.Of("agentSecretSet", fields =>
        {
            secrets.Set(fields.RequireString("account", 128), fields.RequireString("secret", 4_096));
            return new ChangedResponse(true);
        }),
        DesktopAction.Of("agentSecretClear", fields => new ChangedResponse(secrets.Remove(fields.RequireString("account", 128)))),
        DesktopAction.Of("agentConversationStart", fields => repository.Create(fields.RequireString("backendId", 64),
            fields.OptionalText("title", 200, "The title field is invalid.") ?? "New conversation",
            fields.OptionalText("model", 200, "The model is invalid."),
            fields.OptionalText("effort", 16, "The reasoning effort is invalid."),
            fields.OptionalText("contextPreset", 16, "The context preset is invalid.") ?? AgentContextPreset.Balanced)),
        DesktopAction.Of("agentConversationRename", fields => new ChangedResponse(repository.Rename(fields.RequireGuid("conversationId"),
            fields.RequireString("title", 200)))),
        DesktopAction.Of("agentConversationModel", fields =>
        {
            Guid id = fields.RequireGuid("conversationId");
            return runs.WhileIdle(id, () => repository.SetModel(id, fields.RequireString("model", 200),
                fields.OptionalText("effort", 16, "The reasoning effort is invalid.")));
        }),
        DesktopAction.Of("agentConversationContext", fields =>
        {
            Guid id = fields.RequireGuid("conversationId");
            return runs.WhileIdle(id, () => repository.SetContext(id, fields.RequireString("contextPreset", 16)));
        }),
        DesktopAction.Of("agentConversationDelete", fields =>
        {
            Guid id = fields.RequireGuid("conversationId");
            return runs.WhileIdle(id, () => new ChangedResponse(repository.Delete(id)));
        }),
        DesktopAction.Of("agentConversations", fields => new AgentConversationsResponse(repository.List(fields.IntegerOrDefault("limit", 50, 1, 500)))),
        DesktopAction.Of("agentConversation", fields =>
        {
            Guid id = fields.RequireGuid("conversationId");
            return new AgentConversationResponse(repository.Read(id) ?? throw new ArgumentException("Unknown conversation."),
                repository.ReadMessages(id), repository.ReadToolCalls(id, 200), recalls?.Read(id, 12));
        }),
        DesktopAction.Of("agentRecap", fields => new AgentRecapResponse(memory?.ReadRecap(fields.RequireGuid("conversationId")))),
        DesktopAction.Of("agentSend", fields => new AgentRunStartedResponse(runs.Start(fields.RequireGuid("conversationId"),
            fields.RequireString("message", 32_000), fields.OptionalBoolean("useTools") ?? true))),
        DesktopAction.Of("agentRun", fields => runs.Status(fields.RequireGuid("runId"), fields.IntegerOrDefault("after", 0, 0, int.MaxValue))
                                               ?? throw new ArgumentException("Unknown run.")),
        DesktopAction.Of("agentCancel", fields => new ChangedResponse(runs.Cancel(fields.RequireGuid("runId")))),
        DesktopAction.Of("agentProposals", fields => new AgentProposalsResponse(repository.ReadProposals(
            fields.OptionalText("status", 16, "The status field is invalid.") ?? AgentProposal.Pending, fields.IntegerOrDefault("limit", 100, 1, 500)))),
        DesktopAction.Of("agentProposalResolve", fields =>
        {
            AgentProposal proposal = repository.ReadProposal(fields.RequireGuid("proposalId")) ?? throw new ArgumentException("Unknown proposal.");
            bool accept = fields.RequireBoolean("accept");
            if (!repository.SetProposalStatus(proposal.ProposalId, AgentProposal.Pending, accept ? AgentProposal.Applied : AgentProposal.Rejected))
            {
                return new ChangedResponse(false);
            }

            return new ChangedResponse(!accept || proposals.Apply(proposal));
        }),
        DesktopAction.Of("agentMemories", fields => new AgentMemoriesResponse(repository.ReadMemories(
            fields.OptionalText("status", 16, "The status field is invalid."), null, fields.IntegerOrDefault("limit", 100, 1, 500)))),
        DesktopAction.Of("agentMemoryResolve", fields => new ChangedResponse(repository.SetMemoryStatus(fields.RequireGuid("memoryId"),
            fields.RequireBoolean("accept") ? AgentMemory.Active : AgentMemory.Rejected)))
    ];
}
