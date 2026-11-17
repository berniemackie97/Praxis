using System.Net;
using System.Text;
using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Mcp;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Processing;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Agents;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Inference.Ollama;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Finance;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests : IDisposable
{
    private readonly string workspace = Path.Combine(TestPaths.Temp, "praxis-agent-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly string inbox;
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));

    public AgentRuntimeTests()
    {
        root = Path.Combine(workspace, "store");
        inbox = Path.Combine(workspace, "inbox");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(inbox);
        if (!OperatingSystem.IsWindows())
        {
            foreach (string path in new[] { workspace, root, inbox })
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    [Fact]
    public async Task AToolLoopAnswersWithExactFiguresAndCitationsAndPersistsEverything()
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"rent"}"""),
                Call("data_query", """{"dataset":"transactions","filters":[{"field":"direction","op":"eq","values":["outflow"]}],"metrics":[{"function":"sum","field":"amount"}]}""")),
            transcript => new AgentTurn("Your rent note is in lease.md [1]. October outflows were $1,270.20, computed by data_query.", [], 900, 60));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "What did I spend in October and where is my lease?", (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.True(reply.Check.Clean, string.Join(",", reply.Check.UnsupportedFigures));
        Assert.Equal(["knowledge_search", "data_query"], reply.ToolCalls.Select(call => call.Name));
        Assert.NotEmpty(reply.Citations);
        Assert.DoesNotContain("[2]", reply.Text, StringComparison.Ordinal);
        Assert.Equal(1_800, reply.InputTokens);
        IReadOnlyList<AgentMessage> messages = catalog.Agents.ReadMessages(conversation.ConversationId);
        Assert.Equal([AgentRoles.User, AgentRoles.Assistant, AgentRoles.Tool, AgentRoles.Tool, AgentRoles.Assistant], messages.Select(message => message.Role));
        Assert.Contains("\"sum_amount\":-127020", messages[3].Content, StringComparison.Ordinal);
        Assert.Equal(2, catalog.Agents.ReadToolCalls(conversation.ConversationId, 10).Count);
        Assert.Contains("Today is 2026-11-13", backend.SystemPrompts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInventedFigureTriggersOneRepairRound()
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend backend = new(
            _ => Turn(Call("data_query", """{"dataset":"transactions","metrics":[{"function":"sum","field":"amount"}],"filters":[{"field":"direction","op":"eq","values":["outflow"]}]}""")),
            _ => new AgentTurn("You spent $9,999.99 [1].", [], 10, 10),
            transcript =>
            {
                Assert.Equal("How much did I spend?", transcript.Last(message => message.Role == AgentRoles.User).Content);
                return new AgentTurn("You spent $1,270.20 [1].", [], 10, 10);
            });
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "How much did I spend?", (_, _, _) => { }, CancellationToken.None);
        Assert.True(reply.Check.Repaired);
        Assert.True(reply.Check.Clean);
        Assert.Equal("You spent $1,270.20 [1].", reply.Text);
        Assert.Contains("9,999.99", backend.SystemPrompts[2], StringComparison.Ordinal);
        Assert.DoesNotContain(catalog.Agents.ReadMessages(conversation.ConversationId),
            message => message.Content.Contains("9,999.99", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RemoteBackendsSeeOnlyWhatTheDisclosurePolicyAllowsForChat()
    {
        using SqliteCatalog catalog = await SeededAsync();
        string Query(int step) => """{"dataset":"transactions","metrics":[{"function":"count"}]}""";
        ScriptedBackend remote = new(_ => Turn(Call("data_query", Query(0))), _ => new AgentTurn("I can't see your transactions.", [], 1, 1));
        AgentBackendProfile cloud = new("openai-api", AgentBackendKinds.OpenAiCompatible, "https://api.example.com/v1", "model", null, null, true);
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, cloud, remote);
        AgentReply withheld = await runtime.RunAsync(conversation.ConversationId, "Count my transactions", (_, _, _) => { }, CancellationToken.None);
        Assert.True(withheld.WithheldResults > 0);
        Assert.Contains("\"withheld\"", catalog.Agents.ReadMessages(conversation.ConversationId)[2].Content, StringComparison.Ordinal);

        new Application.Policy.PolicyService(catalog.Policy).Request(new PolicyChange(AddRule: new DisclosureRule(Guid.NewGuid(), DataCategories.Financial,
            "openai-api", DisclosurePurposes.Chat, true, null)), PolicyActors.Owner);
        ScriptedBackend granted = new(_ => Turn(Call("data_query", Query(1))), _ => new AgentTurn("You have 4 transactions.", [], 1, 1));
        (AgentRuntime again, AgentConversation second) = Runtime(catalog, cloud, granted);
        AgentReply visible = await again.RunAsync(second.ConversationId, "Count my transactions", (_, _, _) => { }, CancellationToken.None);
        Assert.Equal(0, visible.WithheldResults);
        Assert.Contains("\"count\":4", catalog.Agents.ReadMessages(second.ConversationId)[2].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesRequireAllowEditsAndArtifactsBecomeRevisedSources()
    {
        using SqliteCatalog catalog = await SeededAsync();
        string create = """{"title":"October budget","markdown":"| Category | Planned |\n|---|---|\n| Rent | 1,200.00 |"}""";
        ScriptedBackend denied = new(_ => Turn(Call("artifact_create", create)), _ => new AgentTurn("I need permission to save.", [], 1, 1));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), denied);
        await runtime.RunAsync(conversation.ConversationId, "Save a budget", (_, _, _) => { }, CancellationToken.None);
        Assert.Contains("permission_denied", catalog.Agents.ReadMessages(conversation.ConversationId)[2].Content, StringComparison.Ordinal);
        Assert.Empty(catalog.Agents.ReadArtifacts(10));

        new Application.Policy.PolicyService(catalog.Policy).Request(new PolicyChange(ToolPermission: ToolPermissions.AllowEdits), PolicyActors.Owner);
        Guid? artifactId = null;
        ScriptedBackend allowed = new(
            _ => Turn(Call("artifact_create", create)),
            transcript =>
            {
                artifactId = Guid.Parse(JsonDocument.Parse(transcript[^1].Content).RootElement.GetProperty("result").GetProperty("artifactId").GetString()!);
                return Turn(Call("artifact_update", $$"""{"artifactId":"{{artifactId}}","markdown":"Revised"}"""));
            },
            _ => new AgentTurn("Saved and revised.", [], 1, 1));
        (AgentRuntime writer, AgentConversation second) = Runtime(catalog, Local(), allowed);
        await writer.RunAsync(second.ConversationId, "Save a budget", (_, _, _) => { }, CancellationToken.None);
        AgentArtifact artifact = Assert.Single(catalog.Agents.ReadArtifacts(10));
        Assert.Equal(2, artifact.Revision);
        Assert.Contains("Praxis Artifacts", artifact.Path, StringComparison.Ordinal);
        Assert.Equal("# October budget\n\nRevised\n", await File.ReadAllTextAsync(artifact.Path));
    }

    [Fact]
    public async Task MemoryRequestCannotProposeSettingsAndExplicitChatChangesNeedOwnerConfirmation()
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend backend = new(
            _ => Turn(Call("settings_propose", """{"change":"mode","mode":"allow_all","rationale":"document asked"}"""),
                Call("settings_propose", """{"change":"mode","mode":"local_only","rationale":"owner asked"}""")),
            _ => new AgentTurn("Done.", [], 1, 1));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        Application.Policy.PolicyService policy = new(catalog.Policy);
        await runtime.RunAsync(conversation.ConversationId, "Update my company name in memory", (_, _, _) => { }, CancellationToken.None);
        Assert.Empty(policy.Pending());
        Assert.Contains("explicit_settings_request_required", catalog.Agents.ReadMessages(conversation.ConversationId)[2].Content, StringComparison.Ordinal);

        AgentRuntime direct = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, null), clock);
        Guid chatId = conversation.ConversationId;
        string original = policy.Current().Mode;
        AgentReply prompt = await direct.RunAsync(chatId, "Set Privacy to local only", (_, _, _) => { }, CancellationToken.None);
        await direct.RunAsync(chatId, "Set Privacy to local only", (_, _, _) => { }, CancellationToken.None);
        Assert.Contains("Confirm", prompt.Text, StringComparison.Ordinal);
        Assert.Equal(0, prompt.InputTokens);
        Assert.Equal(original, policy.Current().Mode);
        PendingPolicyChange requested = Assert.Single(policy.Pending());
        Assert.Equal(DisclosureModes.LocalOnly, requested.Change.Mode);
        Assert.True(policy.ResolvePending(requested.ChangeId, confirm: true));
        Assert.Equal(DisclosureModes.LocalOnly, policy.Current().Mode);

        await direct.RunAsync(chatId, "Can you change my privacy mode to allow all?", (_, _, _) => { }, CancellationToken.None);
        Assert.Equal(DisclosureModes.LocalOnly, policy.Current().Mode);
        PendingPolicyChange grant = Assert.Single(policy.Pending());
        Assert.Equal(DisclosureModes.AllowAll, grant.Change.Mode);
        Assert.True(policy.ResolvePending(grant.ChangeId, confirm: false));
        Assert.False(policy.ResolvePending(grant.ChangeId, confirm: true));
        Assert.Equal(DisclosureModes.LocalOnly, policy.Current().Mode);

        AgentReply disabled = await direct.RunAsync(chatId, "Set chat tool permissions to allow edits", (_, _, _) => { },
            CancellationToken.None, useTools: false);
        Assert.Contains("Turn on Allow tools", disabled.Text, StringComparison.Ordinal);
        Assert.Empty(policy.Pending());
    }

    [Fact]
    public async Task ProposalsAndMemoriesRespectToolPermissionAndOwnerReview()
    {
        using SqliteCatalog catalog = await SeededAsync();
        new Application.Policy.PolicyService(catalog.Policy).Request(new PolicyChange(ToolPermission: ToolPermissions.ProposeEdits), PolicyActors.Owner);
        Guid source = catalog.Library.List(10, 0, null).First().SourceId;
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_propose", $$"""{"kind":"source_category","sourceId":"{{source}}","category":"personal","rationale":"lease note"}"""),
                Call("memory_remember", """{"text":"Rent is due on the first of each month."}"""),
                Call("memory_remember", """{"text":"My account is 123456789012."}""")),
            _ => new AgentTurn("Noted.", [], 1, 1));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        await runtime.RunAsync(conversation.ConversationId, "Remember rent timing", (_, _, _) => { }, CancellationToken.None);

        AgentProposal proposal = Assert.Single(catalog.Agents.ReadProposals(AgentProposal.Pending, 10));
        Assert.Equal(AgentProposal.SourceCategory, proposal.Kind);
        Assert.True(new ProposalApplier(catalog.Knowledge, catalog.Policy).Apply(proposal));
        Assert.Equal((DataCategories.Personal, SourceCategory.OwnerOrigin), (catalog.Policy.ReadCategory(source)!.Category, catalog.Policy.ReadCategory(source)!.Origin));
        AgentMemory memory = Assert.Single(catalog.Agents.ReadMemories(null, null, 10));
        Assert.Equal(AgentMemory.Proposed, memory.Status);
        Assert.Contains("account numbers", catalog.Agents.ReadMessages(conversation.ConversationId)[4].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionBackendsExecuteEveryToolThroughPraxis()
    {
        using SqliteCatalog catalog = await SeededAsync();
        SessionBackend session = new(async invoke =>
        {
            string result = await invoke(Call("knowledge_search", """{"query":"lease"}"""), CancellationToken.None);
            Assert.Contains("lease.md", result, StringComparison.Ordinal);
            return new AgentTurn("Your lease is in lease.md [1].", [], 5, 5);
        });
        AgentBackendProfile cli = new("claude-code", AgentBackendKinds.ClaudeCode, null, "sonnet", null, "/usr/bin/true", true);
        catalog.Agents.SaveBackend(cli);
        // A CLI agent is a remote backend: the owner must grant what it may see in chat.
        new Application.Policy.PolicyService(catalog.Policy).Request(new PolicyChange(AddRule: new DisclosureRule(Guid.NewGuid(), DataCategories.Unclassified,
            cli.BackendId, DisclosurePurposes.Chat, true, null)), PolicyActors.Owner);
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);
        AgentConversation conversation = catalog.Agents.Create(cli.BackendId, "cli");
        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Where is my lease?", (_, _, _) => { }, CancellationToken.None);
        Assert.Equal("knowledge_search", Assert.Single(reply.ToolCalls).Name);
        Assert.NotEmpty(reply.Citations);
        Assert.Single(catalog.Agents.ReadToolCalls(conversation.ConversationId, 10));
        Assert.Equal([AgentRoles.User, AgentRoles.Assistant], catalog.Agents.ReadMessages(conversation.ConversationId).Select(message => message.Role));
        Assert.Equal(reply.Text, catalog.Agents.ReadMessages(conversation.ConversationId)[1].Content);
    }


    [Fact]
    public async Task UnexpectedBackendFailureIsTerminalAndDoesNotExposeProviderText()
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend broken = new((_, _) => Task.FromException<AgentTurn>(new IOException("secret provider response: token-123")));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), broken);
        AgentRunCoordinator coordinator = new(runtime, clock);

        AgentRunStatus failed = await Poll(coordinator, coordinator.Start(conversation.ConversationId, "hello"));
        Assert.Equal(AgentRunStatus.Failed, failed.State);
        Assert.Null(failed.Reply);
        Assert.Equal("agent_internal_error", Assert.Single(failed.Events, item => item.Kind == "failed").Detail);
        Assert.DoesNotContain("token-123", string.Join(" ", failed.Events.Select(item => item.Detail)), StringComparison.Ordinal);

        AgentRunStatus retry = await Poll(coordinator, coordinator.Start(conversation.ConversationId, "retry"));
        Assert.Equal(AgentRunStatus.Failed, retry.State);
    }

    [Theory]
    [InlineData(true, "model_timeout")]
    [InlineData(false, "agent_internal_error")]
    public async Task UnexpectedCancellationOrExceptionHasAnActionableBoundedFailure(bool canceled, string expectedCode)
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend backend = new((_, _) => Task.FromException<AgentTurn>(canceled
            ? new OperationCanceledException("server operation timed out")
            : new InvalidDataException("unsafe error details")));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        AgentRunCoordinator coordinator = new(runtime, clock);

        AgentRunStatus failed = await Poll(coordinator, coordinator.Start(conversation.ConversationId, "hello"));
        Assert.Equal(AgentRunStatus.Failed, failed.State);
        Assert.Equal(expectedCode, Assert.Single(failed.Events, item => item.Kind == "failed").Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConversationsKeepTheirChosenModelAndEffortAfterConnectionDefaultsChange(bool session)
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentBackendProfile profile = session
            ? new AgentBackendProfile("codex", AgentBackendKinds.Codex, null, "original", null, "/usr/bin/true", true, "low")
            : Local();
        catalog.Agents.SaveBackend(profile);
        string model = session ? "gpt-6" : "qwen:other";
        string? effort = session ? "high" : null;
        AgentConversation current = catalog.Agents.Create(profile.BackendId, "pinned", model, effort);
        catalog.Agents.SaveBackend(profile with { Model = "changed", Effort = session ? "medium" : null });

        AgentConversation saved = catalog.Agents.Read(current.ConversationId)!;
        Assert.Equal((model, effort), (saved.Model, saved.Effort));
        FixedFactory factory = new(
            session ? null : new ScriptedBackend(_ => new AgentTurn("Local response", [], 1, 1)),
            session ? new SessionBackend(_ => Task.FromResult(new AgentTurn("CLI response", [], 1, 1))) : null);
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), factory, clock);

        AgentReply answer = await runtime.RunAsync(current.ConversationId, "Hello", (_, _, _) => { }, CancellationToken.None);
        Assert.Equal(AgentReply.Completed, answer.StopReason);
        Assert.Equal((model, effort), (factory.LastProfile!.Model, factory.LastProfile.Effort));
        Assert.Equal(answer.Text, catalog.Agents.ReadMessages(current.ConversationId)[1].Content);

        AgentConversation defaulted = catalog.Agents.Create(profile.BackendId, "new default");
        Assert.Equal("changed", defaulted.Model);
        Assert.Equal(session ? "medium" : null, defaulted.Effort);
    }

    [Fact]
    public async Task ConversationsRejectUnconfiguredOrInvalidModelOverrides()
    {
        using SqliteCatalog catalog = await SeededAsync();
        Assert.Throws<ArgumentException>(() => catalog.Agents.Create("missing", "unknown"));
        catalog.Agents.SaveBackend(Local());
        Assert.Throws<ArgumentException>(() => catalog.Agents.Create("local-llm", "bad", "\nmodel", null));
        Assert.Throws<ArgumentException>(() => catalog.Agents.Create("local-llm", "bad", "model", "unknown-effort"));
        catalog.Agents.SaveBackend(Local() with { Enabled = false });
        Assert.Throws<ArgumentException>(() => catalog.Agents.Create("local-llm", "disabled"));
    }

    [Fact]
    public async Task CoordinatorRunsInTheBackgroundReportsEventsAndCancels()
    {
        using SqliteCatalog catalog = await SeededAsync();
        TaskCompletionSource release = new();
        ScriptedBackend slow = new(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return new AgentTurn("done", [], 1, 1);
        });
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), slow);
        AgentRunCoordinator coordinator = new(runtime, clock);
        Guid first = coordinator.Start(conversation.ConversationId, "hello");
        Assert.Throws<InvalidOperationException>(() => coordinator.Start(conversation.ConversationId, "again"));
        Assert.True(coordinator.Cancel(first));
        AgentRunStatus canceled = await Poll(coordinator, first);
        Assert.Equal(AgentRunStatus.Canceled, canceled.State);

        release.SetResult();
        Guid second = coordinator.Start(conversation.ConversationId, "hello again");
        AgentRunStatus completed = await Poll(coordinator, second);
        Assert.Equal(AgentRunStatus.Completed, completed.State);
        Assert.Equal("done", completed.Reply!.Text);
        Assert.Contains(completed.Events, item => item.Kind == "thinking");
        Assert.Empty(coordinator.Status(second, completed.Events[^1].Sequence)!.Events);
    }

    [Fact]
    public async Task DeletingConversationRemovesMessagesAndToolHistoryButPreservesIndependentRecords()
    {
        using SqliteCatalog catalog = await SeededAsync();
        catalog.Agents.SaveBackend(Local());
        AgentConversation conversation = catalog.Agents.Create("local-llm", "Old name");
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromUser("private transcript"));
        catalog.Agents.RecordToolCall(new AgentToolCallRecord(conversation.ConversationId, "c1", "knowledge_search",
            ToolTiers.Read, "{\"query\":\"private\"}", "ok", 18, 0, 0, clock.GetUtcNow(), TimeSpan.FromMilliseconds(1)));
        catalog.Agents.AddMemory(conversation.ConversationId, "Persistent preference", AgentMemory.Active);
        catalog.Agents.AddProposal(conversation.ConversationId, AgentProposal.SourceCategory, "{}", "Review source", AgentProposal.Pending);
        Guid sourceId = catalog.Library.List(10, 0, null)[0].SourceId;
        AgentArtifact artifact = new(Guid.NewGuid(), "Saved report", sourceId, 1, "/private/report.md",
            conversation.ConversationId, clock.GetUtcNow());
        catalog.Agents.SaveArtifact(artifact);

        Assert.True(catalog.Agents.Rename(conversation.ConversationId, "  Revised chat  "));
        Assert.Equal("Revised chat", catalog.Agents.Read(conversation.ConversationId)!.Title);
        Assert.Throws<ArgumentException>(() => catalog.Agents.Rename(conversation.ConversationId, "  "));
        Assert.True(catalog.Agents.Delete(conversation.ConversationId));
        Assert.False(catalog.Agents.Delete(conversation.ConversationId));
        Assert.Null(catalog.Agents.Read(conversation.ConversationId));
        Assert.Empty(catalog.Agents.ReadMessages(conversation.ConversationId));
        Assert.Empty(catalog.Agents.ReadToolCalls(conversation.ConversationId, 100));
        Assert.DoesNotContain(catalog.Agents.List(100), row => row.ConversationId == conversation.ConversationId);
        Assert.Null(Assert.Single(catalog.Agents.ReadMemories(null, null, 10)).ConversationId);
        Assert.Null(Assert.Single(catalog.Agents.ReadProposals(AgentProposal.Pending, 10)).ConversationId);
        Assert.Null(catalog.Agents.ReadArtifact(artifact.ArtifactId)!.ConversationId);
    }

    [Fact]
    public async Task DeletionIsRejectedWhileTheConversationHasARunningResponse()
    {
        using SqliteCatalog catalog = await SeededAsync();
        TaskCompletionSource release = new();
        ScriptedBackend slow = new(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return new AgentTurn("done", [], 1, 1);
        });
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), slow);
        AgentRunCoordinator coordinator = new(runtime, clock);
        Guid running = coordinator.Start(conversation.ConversationId, "hello");
        Assert.True(coordinator.HasRunning(conversation.ConversationId));
        Assert.True(coordinator.Cancel(running));
        Assert.Equal(AgentRunStatus.Canceled, (await Poll(coordinator, running)).State);
        Assert.False(coordinator.HasRunning(conversation.ConversationId));
        Assert.True(catalog.Agents.Delete(conversation.ConversationId));
        release.SetResult();
    }

    [Fact]
    public async Task McpRelayBridgesStdioToTheSessionSocketOnlyWithItsToken()
    {
        McpServerProtocol protocol = new([], (_, _) => Task.FromResult("{}"));
        await using AgentToolSocketHost host = new(Path.GetTempPath(), protocol);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(host.Directory));
        }

        string Exchange(string token)
        {
            using MemoryStream input = new(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}\n"));
            using MemoryStream output = new();
            Assert.Equal(0, McpRelay.RunAsync(host.SocketPath, token, input, output, CancellationToken.None).GetAwaiter().GetResult());
            return Encoding.UTF8.GetString(output.ToArray());
        }

        Assert.Contains("\"result\":{}", Exchange(host.Token), StringComparison.Ordinal);
        Assert.Equal("", Exchange(new string('0', 64)));
        string directory = host.Directory;
        await host.DisposeAsync();
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task HttpBackendsSpeakEachProvidersToolProtocol()
    {
        CapturingHandler openAiHandler = new("""
            {"choices":[{"message":{"content":"","tool_calls":[{"id":"c1","type":"function","function":{"name":"data_query","arguments":"{\"dataset\":\"transactions\"}"}}]}}],
             "usage":{"prompt_tokens":120,"completion_tokens":8}}
            """);
        OpenAiCompatibleAgentBackend openAi = new(new HttpClient(openAiHandler), new Uri("https://api.example.com/v1/"), "gpt-x", "secret");
        AgentToolDefinition tool = new("data_query", "q", JsonDocument.Parse("""{"type":"object"}""").RootElement, ToolTiers.Read);
        AgentMessage[] transcript =
        [
            AgentMessage.FromUser("hi"), AgentMessage.FromAssistant("", [new AgentToolCall("c0", "data_query", "{}")]),
            AgentMessage.FromTool("c0", "{\"result\":1}", []), new AgentMessage(AgentRoles.Summary, "earlier", [], null, [])
        ];
        AgentTurn turn = await openAi.CompleteAsync("system", transcript, [tool], CancellationToken.None);
        Assert.Equal("data_query", Assert.Single(turn.ToolCalls).Name);
        Assert.Equal(120, turn.InputTokens);
        AgentRequestFootprint openAiBytes = Assert.IsType<AgentRequestFootprint>(turn.Request);
        Assert.Equal(Encoding.UTF8.GetByteCount(openAiHandler.Body!), openAiBytes.RequestBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount("system"), openAiBytes.SystemTextBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount("""{"result":1}"""), openAiBytes.ToolResultContentBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount("{}"), openAiBytes.ToolCallArgumentsBytes);
        Assert.Equal(1, openAiBytes.AdvertisedTools);
        Assert.Equal("https://api.example.com/v1/chat/completions", openAiHandler.Uri!.ToString());
        Assert.Equal("Bearer secret", openAiHandler.Authorization);
        using (JsonDocument body = JsonDocument.Parse(openAiHandler.Body!))
        {
            Assert.Equal("tool", body.RootElement.GetProperty("messages")[3].GetProperty("role").GetString());
            Assert.Equal("data_query", body.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
            Assert.Equal(Encoding.UTF8.GetByteCount(body.RootElement.GetProperty("messages").GetRawText()), openAiBytes.MessageBytes);
            Assert.Equal(Encoding.UTF8.GetByteCount(body.RootElement.GetProperty("tools").GetRawText()), openAiBytes.ToolSchemaBytes);
        }

        CapturingHandler anthropicHandler = new("""
            {"content":[{"type":"text","text":"Checking."},{"type":"tool_use","id":"t1","name":"data_query","input":{"dataset":"accounts"}}],"usage":{"input_tokens":50,"output_tokens":4}}
            """);
        AnthropicAgentBackend anthropic = new(new HttpClient(anthropicHandler), new Uri("https://api.anthropic.com/"), "claude-x", "key");
        AgentTurn claude = await anthropic.CompleteAsync("system", transcript, [tool], CancellationToken.None);
        Assert.Equal("Checking.", claude.Text);
        Assert.Equal("{\"dataset\":\"accounts\"}", Assert.Single(claude.ToolCalls).ArgumentsJson);
        using JsonDocument anthropicBody = JsonDocument.Parse(anthropicHandler.Body!);
        JsonElement messages = anthropicBody.RootElement.GetProperty("messages");
        AgentRequestFootprint anthropicBytes = Assert.IsType<AgentRequestFootprint>(claude.Request);
        Assert.Equal(Encoding.UTF8.GetByteCount(anthropicHandler.Body!), anthropicBytes.RequestBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(messages.GetRawText()), anthropicBytes.MessageBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(anthropicBody.RootElement.GetProperty("tools").GetRawText()), anthropicBytes.ToolSchemaBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount("""{"result":1}"""), anthropicBytes.ToolResultContentBytes);
        Assert.Equal(1, anthropicBytes.AdvertisedTools);
        Assert.Equal(["user", "assistant", "user"], messages.EnumerateArray().Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("tool_result", messages[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("key", anthropicHandler.ApiKey);

        CapturingHandler failing = new("{}", HttpStatusCode.TooManyRequests);
        Application.Inference.InferenceException quota = await Assert.ThrowsAsync<Application.Inference.InferenceException>(() =>
            new OpenAiCompatibleAgentBackend(new HttpClient(failing), new Uri("https://api.example.com/v1/"), "m", null).CompleteAsync("s", transcript, [], CancellationToken.None));
        Assert.Equal("model_quota_exhausted", quota.Code);
    }

    private async Task<SqliteCatalog> SeededAsync()
    {
        SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        await Capture(catalog, "download.qfx", Encoding.UTF8.GetBytes(StructuredExportParserTests.SgmlOfx));
        await Capture(catalog, "lease.md", Encoding.UTF8.GetBytes("# Lease\nMonthly rent is paid by check to the landlord."));
        IReadOnlyList<IProcessingHandler> handlers = CatalogProcessing.CreateHandlers(catalog, new ScriptedModelFactory(), clock);
        await ProcessingDriver.RunIntakeAsync(catalog, handlers);

        foreach (Guid id in catalog.Library.List(10, 0, null).Where(item => item.SourceLocator.EndsWith(".qfx", StringComparison.Ordinal)).Select(item => item.SourceId))
        {
            Assert.Equal(DataCategories.Financial, catalog.Policy.ReadCategory(id)?.Category);
        }

        return catalog;
    }

    private (AgentRuntime, AgentConversation) Runtime(SqliteCatalog catalog, AgentBackendProfile profile, ScriptedBackend backend)
    {
        catalog.Agents.SaveBackend(profile);
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(backend, null), clock);
        return (runtime, catalog.Agents.Create(profile.BackendId, "test"));
    }

    private IReadOnlyList<IAgentTool> Tools(SqliteCatalog catalog) =>
        AgentComposition.Tools(catalog, new OllamaEmbeddingModelConnector(), new ArtifactWriter(catalog.Originals, root, inbox), new ScriptedModelFactory(), clock);

    private static AgentBackendProfile Local() => new("local-llm", AgentBackendKinds.OpenAiCompatible, "http://127.0.0.1:11434/v1", "qwen", null, null, true);

    private static AgentTurn Turn(params AgentToolCall[] calls) => new("", calls, 900, 30);

    private static AgentToolCall Call(string name, string arguments) => new(Guid.NewGuid().ToString("N")[..8], name, arguments);

    private async Task Capture(SqliteCatalog catalog, string name, byte[] bytes)
    {
        using MemoryStream input = new(bytes);
        await catalog.Originals.CaptureFileAsync(Path.Combine(inbox, name), input, bytes.Length, name);
    }

    private static async Task<AgentRunStatus> Poll(AgentRunCoordinator coordinator, Guid run)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            AgentRunStatus status = coordinator.Status(run, 0)!;
            if (status.State != AgentRunStatus.Running)
            {
                return status;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The run did not finish.");
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }

    private sealed class ScriptedBackend : IAgentBackend
    {
        private readonly Queue<Func<IReadOnlyList<AgentMessage>, CancellationToken, Task<AgentTurn>>> turns;

        public ScriptedBackend(params Func<IReadOnlyList<AgentMessage>, AgentTurn>[] steps) =>
            turns = new(steps.Select(step => (Func<IReadOnlyList<AgentMessage>, CancellationToken, Task<AgentTurn>>)((transcript, _) => Task.FromResult(step(transcript)))));

        public ScriptedBackend(Func<IReadOnlyList<AgentMessage>, CancellationToken, Task<AgentTurn>> step) => turns = new([step]);

        public List<string> SystemPrompts { get; } = [];
        public List<int> ToolCounts { get; } = [];
        public List<IReadOnlyList<AgentToolDefinition>> ToolSets { get; } = [];
        public bool AllowToollessTurn { get; set; }
        public string ExpectedTool { get; set; } = "data_query";

        public Task<AgentTurn> CompleteAsync(string systemPrompt, IReadOnlyList<AgentMessage> transcript, IReadOnlyList<AgentToolDefinition> tools, CancellationToken cancellationToken)
        {
            lock (SystemPrompts)
            {
                SystemPrompts.Add(systemPrompt);
                ToolCounts.Add(tools.Count);
                ToolSets.Add(tools.ToArray());
            }
            if (!AllowToollessTurn || tools.Count > 0)
            {
                Assert.Contains(tools, tool => tool.Name == ExpectedTool);
            }
            return turns.Count > 1 ? turns.Dequeue()(transcript, cancellationToken) : turns.Peek()(transcript, cancellationToken);
        }
    }

    private sealed class SessionBackend(Func<Func<AgentToolCall, CancellationToken, Task<string>>, Task<AgentTurn>> run,
        Action<IReadOnlyList<AgentMessage>>? inspect = null, Action<string, IReadOnlyList<AgentToolDefinition>>? inspectRequest = null) : ISessionAgentBackend
    {
        public Task<AgentTurn> RunAsync(string systemPrompt, IReadOnlyList<AgentMessage> transcript, IReadOnlyList<AgentToolDefinition> tools,
            Func<AgentToolCall, CancellationToken, Task<string>> invoke, CancellationToken cancellationToken)
        {
            inspect?.Invoke(transcript);
            inspectRequest?.Invoke(systemPrompt, tools);
            return run(invoke);
        }
    }

    private sealed class FixedFactory(IAgentBackend? turn, ISessionAgentBackend? session) : IAgentBackendFactory
    {
        public AgentBackendProfile? LastProfile { get; private set; }

        public IAgentBackend? CreateTurnBackend(AgentBackendProfile profile)
        {
            LastProfile = profile;
            return session is null ? turn : null;
        }

        public ISessionAgentBackend? CreateSessionBackend(AgentBackendProfile profile)
        {
            LastProfile = profile;
            return session;
        }
    }

    private sealed class CapturingHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public string? ApiKey { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            ApiKey = request.Headers.TryGetValues("x-api-key", out IEnumerable<string>? values) ? values.First() : null;
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
