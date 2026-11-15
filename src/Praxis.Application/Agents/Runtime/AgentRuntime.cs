using Praxis.Application.Agents.Tools;
using Praxis.Application.Policy;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Runtime;

/// <summary>
/// Runs one user message to a verified answer. Praxis-driven backends get a bounded tool loop;
/// CLI backends run their own loop with every tool call executed here. Either way: disclosure is
/// scoped to the backend for purpose chat, permission tiers are enforced by the registry, every
/// call is audited, evidence is numbered, long transcripts are compacted, and the final answer is
/// checked (figures must come from tools; evidence must be cited) with one repair round.
/// </summary>
public sealed class AgentRuntime(
    IAgentConversationRepository conversations, AgentToolRegistry registry, IAgentBackendFactory backends, PolicyService policy,
    DisclosureGate gate, TimeProvider clock)
{
    private const int MaximumCliHistoryCharacters = 24_000;
    private const int MaximumCliToolCalls = 12;

    public async Task<AgentReply> RunAsync(Guid conversationId, string userText, Action<string, string?, string?> report, CancellationToken cancellationToken,
        AgentRunLimits? limits = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AgentRunLimits bounds = limits ?? new AgentRunLimits();
        if (string.IsNullOrWhiteSpace(userText) || userText.Length > 32_000)
        {
            throw new ArgumentException("Send a message of 1–32,000 characters.");
        }

        AgentConversation conversation = conversations.Read(conversationId) ?? throw new ArgumentException("Unknown conversation.");
        AgentBackendProfile configured = conversations.ReadBackends().FirstOrDefault(item => item.BackendId == conversation.BackendId && item.Enabled)
            ?? throw new InvalidOperationException("This conversation's backend is not configured or is disabled.");
        AgentBackendProfile profile = conversation.Model is { } pinnedModel
            ? configured with { Model = pinnedModel, Effort = conversation.Effort }
            : configured;
        string permission = policy.Current().ToolPermission;
        DisclosureScope scope = new(gate, profile.BackendId, profile.IsLocal);
        AgentToolContext context = new(conversationId, profile.BackendId, profile.IsLocal, permission, scope.MayDisclose);
        string system = AgentSystemPrompt.Build(clock.GetUtcNow(), permission,
            profile.IsLocal ? conversations.ReadMemories(AgentMemory.Active, null, 12) : []);
        conversations.Append(conversationId, AgentMessage.FromUser(userText));

        RunState state = new(new CitationLedger());
        AgentToolDefinition[] definitions = registry.Definitions
            .Where(tool => profile.IsLocal || tool.Name != MemoryRecallTool.ToolName).ToArray();
        if (backends.CreateSessionBackend(profile) is { } session)
        {
            AgentContextWindow window = AgentContextWindow.ForCli(Transcript(conversationId),
                Math.Min(bounds.CompactAfterCharacters, MaximumCliHistoryCharacters));
            state.OmittedMessages = window.OmittedMessages;
            if (window.OmittedMessages > 0)
            {
                report("context_limited", null, $"{window.OmittedMessages} older messages omitted");
            }

            string sessionSystem = window.OmittedMessages == 0 ? system : system +
                "\n\nSome earlier conversation messages were omitted to fit this request. Do not assume their contents or invent earlier decisions; ask the owner for missing context when necessary.";
            report("thinking", null, profile.BackendId);
            AgentToolDefinition[] permitted = definitions.Where(tool => ToolTiers.Permits(permission, tool.Tier)).ToArray();
            int toolLimit = Math.Min(bounds.MaximumToolCalls, MaximumCliToolCalls);
            SemaphoreSlim toolGate = new(1);
            async Task<string> InvokeSessionTool(AgentToolCall call, CancellationToken token)
            {
                await toolGate.WaitAsync(token);
                try
                {
                    return state.ToolCalls.Count >= toolLimit
                        ? """{"error":"tool_budget_exhausted","message":"Answer with what you have."}"""
                        : await InvokeAsync(call, context, state, report, token);
                }
                finally
                {
                    toolGate.Release();
                }
            }

            AgentTurn turn = await session.RunAsync(sessionSystem, window.Messages, permitted, InvokeSessionTool, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            state.InputTokens += turn.InputTokens;
            state.OutputTokens += turn.OutputTokens;
            conversations.Append(conversationId, AgentMessage.FromAssistant(turn.Text, []));
            return Finish(conversationId, userText, turn.Text, AgentReply.Completed, state, report, repaired: false);
        }

        IAgentBackend backend = backends.CreateTurnBackend(profile) ?? throw new InvalidOperationException("The backend kind is not supported.");
        await CompactIfNeededAsync(conversationId, backend, system, bounds, report, cancellationToken);
        bool repaired = false;
        for (int step = 0; step < bounds.MaximumSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.InputTokens > bounds.MaximumInputTokens)
            {
                return Finish(conversationId, userText, "I stopped because this request reached its token budget.", AgentReply.TokenBudget, state, report, repaired);
            }

            report("thinking", null, null);
            AgentTurn turn = await backend.CompleteAsync(system, Transcript(conversationId), definitions, cancellationToken);
            state.InputTokens += turn.InputTokens;
            state.OutputTokens += turn.OutputTokens;
            cancellationToken.ThrowIfCancellationRequested();
            conversations.Append(conversationId, AgentMessage.FromAssistant(turn.Text, turn.ToolCalls));
            if (turn.ToolCalls.Count == 0)
            {
                AnswerCheck check = Check(turn.Text, userText, state, repaired);
                if (!check.Clean && !repaired)
                {
                    report("repairing", null, string.Join(", ", check.UnsupportedFigures));
                    conversations.Append(conversationId, AgentMessage.FromUser(AnswerVerifier.RepairInstruction(check)));
                    repaired = true;
                    continue;
                }

                return Finish(conversationId, userText, turn.Text, AgentReply.Completed, state, report, repaired);
            }

            foreach (AgentToolCall call in turn.ToolCalls)
            {
                string content = state.ToolCalls.Count >= bounds.MaximumToolCalls
                    ? """{"error":"tool_budget_exhausted","message":"Answer with what you have."}"""
                    : await InvokeAsync(call, context, state, report, cancellationToken);
                conversations.Append(conversationId, AgentMessage.FromTool(call.CallId, content, []));
            }
        }

        return Finish(conversationId, userText, "I stopped because this request reached its step limit. Ask me to continue.", AgentReply.StepLimit, state, report, repaired);
    }

    private async Task<string> InvokeAsync(AgentToolCall call, AgentToolContext context, RunState state, Action<string, string?, string?> report, CancellationToken cancellationToken)
    {
        report("tool_started", call.Name, null);
        AgentToolOutput output = await registry.InvokeAsync(call, context, cancellationToken);
        state.ToolCalls.Add(call);
        state.Withheld += output.Withheld.Sum(notice => notice.Count);
        string wrapped = state.Citations.Wrap(output);
        state.ToolResults.Add(output.Content);
        report("tool_finished", call.Name, output.Withheld.Count > 0 ? $"withheld {output.Withheld.Sum(notice => notice.Count)}" : null);
        return wrapped;
    }

    private AgentReply Finish(Guid conversationId, string userText, string text, string stop, RunState state, Action<string, string?, string?> report, bool repaired)
    {
        report("checking", null, null);
        AnswerCheck check = Check(text, userText, state, repaired);
        report("completed", null, check.Clean ? null : "answer check flagged figures");
        return new AgentReply(conversationId, text, stop, state.ToolCalls, state.Citations.All, state.Withheld, check, state.InputTokens, state.OutputTokens, state.OmittedMessages);
    }

    private static AnswerCheck Check(string text, string userText, RunState state, bool repaired) =>
        AnswerVerifier.Check(text, state.ToolResults, userText, state.Citations.All.Count > 0, repaired);

    /// <summary>The transcript from the latest compaction summary onward.</summary>
    private IReadOnlyList<AgentMessage> Transcript(Guid conversationId)
    {
        IReadOnlyList<AgentMessage> messages = conversations.ReadMessages(conversationId);
        int start = 0;
        for (int index = messages.Count - 1; index >= 0; index--)
        {
            if (messages[index].Role == AgentRoles.Summary)
            {
                start = index;
                break;
            }
        }

        return messages.Skip(start).ToArray();
    }

    private async Task CompactIfNeededAsync(Guid conversationId, IAgentBackend backend, string system, AgentRunLimits bounds,
        Action<string, string?, string?> report, CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentMessage> transcript = Transcript(conversationId);
        if (transcript.Sum(message => message.Content.Length) <= bounds.CompactAfterCharacters || transcript.Count < 12)
        {
            return;
        }

        // Keep a recent tail that starts at a user message, so tool results stay paired with their calls.
        int tail = transcript.Count - 6;
        while (tail > 0 && transcript[tail].Role != AgentRoles.User)
        {
            tail--;
        }

        if (tail <= 1)
        {
            return;
        }

        report("compacting", null, null);
        AgentMessage[] older = transcript.Take(tail).ToArray();
        AgentTurn summary = await backend.CompleteAsync(system,
            [.. older, AgentMessage.FromUser("Summarize the conversation so far for your own future reference: the owner's goals, decisions, facts established with their [n] citations, and open questions. Be concise and exact.")],
            [], cancellationToken);
        conversations.Append(conversationId, new AgentMessage(AgentRoles.Summary, summary.Text, [], null, []));
        foreach (AgentMessage message in transcript.Skip(tail))
        {
            conversations.Append(conversationId, message);
        }
    }

    private sealed class RunState(CitationLedger citations)
    {
        public CitationLedger Citations { get; } = citations;
        public List<AgentToolCall> ToolCalls { get; } = [];
        public List<string> ToolResults { get; } = [];
        public int Withheld { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int OmittedMessages { get; set; }
    }
}
