using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Capabilities;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Agents.Tools;
using Praxis.Application.Policy;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Agents.Capabilities;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    [Theory]
    [InlineData(AgentBackendKinds.Codex, false)]
    [InlineData(AgentBackendKinds.ClaudeCode, true)]
    public async Task CliAutomaticallyReceivesOnlyCurrentGrantedExcerptsAndRecordsItsSelection(string kind, bool useTools)
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentBackendProfile profile = new(kind, kind, null, "default", null, "/usr/bin/true", true);
        catalog.Agents.SaveBackend(profile);
        AgentConversation chat = catalog.Agents.Create(profile.BackendId, "brain context");
        Guid source = Assert.Single(catalog.Search.Query("lease", 10)).SourceId;
        catalog.Policy.SaveCategory(new SourceCategory(source, DataCategories.Documents, SourceCategory.OwnerOrigin, false));
        PolicyService policy = new(catalog.Policy);
        policy.Request(new PolicyChange(Mode: DisclosureModes.StandingGrants), PolicyActors.Owner);
        policy.Request(new PolicyChange(AddRule: new DisclosureRule(Guid.NewGuid(), DataCategories.Documents, kind, DisclosurePurposes.Chat,
            true, null)), PolicyActors.Owner);

        IReadOnlyList<AgentMessage> delivered = [];
        SessionBackend session = new(_ => Task.FromResult(new AgentTurn("The lease says rent is paid by check [1].", [], 0, 0)),
            messages => delivered = messages);
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);
        const string question = "How is rent paid under the lease?";
        AgentReply reply = await runtime.RunAsync(chat.ConversationId, question, (_, _, _) => { }, CancellationToken.None, useTools: useTools);
        Assert.Contains(delivered, message => message.Content.Contains("Monthly rent is paid by check", StringComparison.Ordinal));
        Assert.Equal(question, delivered[^1].Content);
        Assert.Equal(source, Assert.Single(reply.Citations).SourceId);
        AgentRecallReceipt receipt = Assert.Single(catalog.AgentRecalls.Read(chat.ConversationId, 10));
        Assert.Equal(kind, receipt.BackendId);
        Assert.Equal(source, Assert.Single(receipt.Selected).SourceId);
        Assert.Equal(0, receipt.Withheld);
        Assert.DoesNotContain(catalog.Agents.ReadMessages(chat.ConversationId), message =>
            message.Content.Contains("Monthly rent is paid by check", StringComparison.Ordinal));

        catalog.Policy.SaveCategory(new SourceCategory(source, DataCategories.Documents, SourceCategory.OwnerOrigin, true));
        await runtime.RunAsync(chat.ConversationId, question, (_, _, _) => { }, CancellationToken.None, useTools: useTools);
        Assert.DoesNotContain(delivered, message => message.Content.Contains("Monthly rent is paid by check", StringComparison.Ordinal));
        Assert.DoesNotContain(delivered, message => message.Role == AgentRoles.Assistant &&
            message.Content.Contains("paid by check", StringComparison.Ordinal));
        AgentRecallReceipt revoked = catalog.AgentRecalls.Read(chat.ConversationId, 10)[0];
        Assert.Equal("withheld", revoked.Outcome);
        Assert.Empty(revoked.Selected);
        Assert.Equal(1, revoked.Withheld);
    }

    [Theory]
    [InlineData("Reply with PING only.", false)]
    [InlineData("Reply with PING only.", true)]
    [InlineData("reply with only ping", false)]
    [InlineData("reply with only pong", true)]
    [InlineData("Reply with lease only.", false)]
    [InlineData("Please reply with only lease.", false)]
    [InlineData("Reply only with lease.", false)]
    [InlineData("Say just lease.", false)]
    [InlineData("Reply with just lease.", false)]
    [InlineData("Say 'lease' only.", false)]
    [InlineData("Respond with \"Lease remains private\" exactly.", false)]
    [InlineData("Hello!", false)]
    public async Task SourceIndependentChatDoesNotDiscloseIncidentalBrainMatches(string prompt, bool useTools)
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentBackendProfile profile = new("codex", AgentBackendKinds.Codex, null, "default", null, "/usr/bin/true", true);
        catalog.Agents.SaveBackend(profile);
        AgentConversation chat = catalog.Agents.Create(profile.BackendId, "direct reply");
        Guid lease = Assert.Single(catalog.Search.Query("lease", 10)).SourceId;
        catalog.Policy.SaveCategory(new SourceCategory(lease, DataCategories.Documents, SourceCategory.OwnerOrigin, false));
        PolicyService policy = new(catalog.Policy);
        policy.Request(new PolicyChange(Mode: DisclosureModes.StandingGrants), PolicyActors.Owner);
        policy.Request(new PolicyChange(AddRule: new DisclosureRule(Guid.NewGuid(), DataCategories.Documents, profile.BackendId,
            DisclosurePurposes.Chat, true, null)), PolicyActors.Owner);

        IReadOnlyList<AgentMessage> delivered = [];
        SessionBackend session = new(_ => Task.FromResult(new AgentTurn("OK", [], 5, 2)),
            messages => delivered = messages);
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);
        AgentReply reply = await runtime.RunAsync(chat.ConversationId, prompt, (_, _, _) => { }, CancellationToken.None, useTools: useTools);

        Assert.Equal(prompt, Assert.Single(delivered).Content);
        Assert.Equal("OK", reply.Text);
        Assert.Empty(reply.Citations);
        AgentRecallReceipt receipt = Assert.Single(catalog.AgentRecalls.Read(chat.ConversationId, 10));
        Assert.Equal("not_needed", receipt.Outcome);
        Assert.Equal(0, receipt.Considered);
        Assert.Empty(receipt.Selected);
    }

    [Theory]
    [InlineData("Hello, what does the lease say?")]
    [InlineData("Reply with only the rent specified in my lease.")]
    public async Task SourceDependentRequestStillRecallsTheSource(string prompt)
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentBackendProfile profile = Local() with { BackendId = "ollama" };
        catalog.Agents.SaveBackend(profile);
        AgentConversation chat = catalog.Agents.Create(profile.BackendId, "grounded");
        IReadOnlyList<AgentMessage> delivered = [];
        SessionBackend session = new(_ => Task.FromResult(new AgentTurn("Rent is paid by check [1].", [], 5, 2)),
            messages => delivered = messages);
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);

        AgentReply reply = await runtime.RunAsync(chat.ConversationId, prompt,
            (_, _, _) => { }, CancellationToken.None, useTools: false);

        Assert.Contains(delivered, message => message.Content.Contains("Monthly rent is paid by check", StringComparison.Ordinal));
        Assert.Equal(prompt, delivered[^1].Content);
        Assert.Single(reply.Citations);
        AgentRecallReceipt receipt = Assert.Single(catalog.AgentRecalls.Read(chat.ConversationId, 10));
        Assert.Single(receipt.Selected);
        Assert.Contains(receipt.Outcome, new[] { "full_match", "focused_match" });
    }

    [Theory]
    [InlineData("What is Conquer Online?", false)]
    [InlineData("Explain Conquer Online.", false)]
    [InlineData("I wonder what Conquer Online is.", false)]
    [InlineData("I was wondering what Conquer Online is.", false)]
    [InlineData("I'm curious: what is Conquer Online?", false)]
    [InlineData("I wonder what my OpenConquerServer project says about Conquer Online.", true)]
    [InlineData("What is Conquer Online, and how does my OpenConquerServer project relate to it?", true)]
    [InlineData("What does my OpenConquerServer documentation say about Conquer Online?", true)]
    public async Task GeneralAndOwnerScopedQuestionsDoNotConflatePrivateProjectWithPublicTopic(
        string question, bool ownerScoped)
    {
        using SqliteCatalog catalog = await SeededAsync();
        const string sourceText = "Conquer Online is a game. OpenConquerServer is a .NET server project related to Conquer Online.";
        using MemoryStream document = new(System.Text.Encoding.UTF8.GetBytes(sourceText));
        Guid sourceId = (await catalog.Originals.CaptureFileAsync(
            Path.Combine(inbox, "conquer-project-notes.md"), document, document.Length, "project-note")).SourceId;
        Assert.True(await Praxis.Infrastructure.Tests.Support.ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.NotEmpty(catalog.Search.Query("Conquer Online", 10));

        AgentBackendProfile cloud = new("codex", AgentBackendKinds.Codex, null, "default", null, "/usr/bin/true", true);
        catalog.Agents.SaveBackend(cloud);
        catalog.Policy.SaveCategory(new SourceCategory(sourceId, DataCategories.Documents, SourceCategory.OwnerOrigin, false));
        PolicyService policy = new(catalog.Policy);
        policy.Request(new PolicyChange(Mode: DisclosureModes.StandingGrants), PolicyActors.Owner);
        policy.Request(new PolicyChange(AddRule: new DisclosureRule(Guid.NewGuid(), DataCategories.Documents,
            cloud.BackendId, DisclosurePurposes.Chat, true, null)), PolicyActors.Owner);
        AgentConversation conversation = catalog.Agents.Create(cloud.BackendId, "disambiguation");
        IReadOnlyList<AgentMessage> delivered = [];
        SessionBackend session = new(_ => Task.FromResult(new AgentTurn(
            ownerScoped ? "The owner document relates the game and software project [1]." : "Conquer Online is a game.",
            [], 6, 3)), messages => delivered = messages);
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog),
            new FixedFactory(null, session), clock);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, question,
            (_, _, _) => { }, CancellationToken.None, useTools: false);
        AgentRecallReceipt receipt = Assert.Single(catalog.AgentRecalls.Read(conversation.ConversationId, 10));
        Assert.Equal(ownerScoped, receipt.Selected.Count > 0);
        Assert.Contains(receipt.Outcome, ownerScoped ? new[] { "full_match", "focused_match" } : new[] { "not_needed" });
        Assert.Equal(ownerScoped, delivered.Any(message =>
            message.Content.Contains("OpenConquerServer is a .NET server project", StringComparison.Ordinal)));
        Assert.Equal(ownerScoped, reply.Citations.Count > 0);
        Assert.Equal(question, delivered[^1].Content);
        Assert.Equal(AgentReply.Completed, reply.StopReason);
    }

    [Fact]
    public async Task AutomaticRecallRanksMatchingPassageAboveMatchingFilename()
    {
        using SqliteCatalog catalog = await SeededAsync();
        using MemoryStream misleading = new(System.Text.Encoding.UTF8.GetBytes(
            "This note covers unrelated UI formatting and shell themes."));
        using MemoryStream relevant = new(System.Text.Encoding.UTF8.GetBytes(
            "Conquer Online walking packet behavior: entity movement and direction validation."));

        await catalog.Originals.CaptureFileAsync(Path.Combine(inbox, "conquer-online.md"),
            misleading, misleading.Length, "misleading");
        Guid relevantSource = (await catalog.Originals.CaptureFileAsync(
            Path.Combine(inbox, "world-walking-notes.md"), relevant, relevant.Length, "walking-evidence")).SourceId;
        Praxis.Infrastructure.Tests.Support.ProcessingDriver worker =
            Praxis.Infrastructure.Tests.Support.ProcessingDriver.Text(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());

        AgentBackendProfile profile = Local() with { BackendId = "ollama" };
        catalog.Agents.SaveBackend(profile);
        AgentConversation conversation = catalog.Agents.Create(profile.BackendId, "ranking");
        IReadOnlyList<AgentMessage> forwarded = [];
        SessionBackend session = new(_ => Task.FromResult(
            new AgentTurn("The notes discuss walking behavior [1].", [], 6, 3)), messages => forwarded = messages);
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog),
            new FixedFactory(null, session), clock);

        AgentReply answer = await runtime.RunAsync(conversation.ConversationId,
            "What do my notes say about Conquer Online walking packets?",
            (_, _, _) => { }, CancellationToken.None, useTools: false);

        AgentRecallReceipt receipt = Assert.Single(catalog.AgentRecalls.Read(conversation.ConversationId, 10));
        Assert.Equal(relevantSource, receipt.Selected[0].SourceId);
        Assert.Equal(relevantSource, answer.Citations[0].SourceId);
        string brief = Assert.Single(forwarded, message =>
            message.Content.StartsWith("Local library search excerpts", StringComparison.Ordinal)).Content;
        int relevantIndex = brief.IndexOf("world-walking-notes.md", StringComparison.Ordinal);
        int titleOnlyIndex = brief.IndexOf("conquer-online.md", StringComparison.Ordinal);
        Assert.True(relevantIndex >= 0 && (titleOnlyIndex < 0 || relevantIndex < titleOnlyIndex));
    }

    [Fact]
    public async Task TerminalActionRequiresOwnerDecisionRunsOnceAndOnlyReturnsOutputToLocalChat()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        RecordingCapabilityExecutor executor = new();
        CapabilityService capabilities = new(catalog.Capabilities, executor);
        Guid chat = Guid.NewGuid();
        AgentToolContext local = new(chat, "local", true, ToolPermissions.AllowEdits, _ => true, "Run a terminal command.");
        CapabilityRequestTool request = new(capabilities);
        JsonElement arguments = ToolArguments.Schema("""{"operation":"terminal","target":"printf marker"}""");
        await request.InvokeAsync(arguments, local, CancellationToken.None);
        await request.InvokeAsync(arguments, local, CancellationToken.None);
        CapabilityRequest queued = Assert.Single(capabilities.List(10));
        Assert.Equal("pending", queued.Status);
        Assert.Equal(0, executor.Invocations);

        Assert.Equal("accepted", (await capabilities.ResolveAsync(queued.Id, true, CancellationToken.None))?.Status);
        Assert.Null(await capabilities.ResolveAsync(queued.Id, true, CancellationToken.None));
        Assert.Equal(1, executor.Invocations);
        CapabilityResultTool result = new(capabilities);
        JsonElement id = ToolArguments.Schema(ToolJson.Serialize(new { queued.Id }));
        Assert.Contains("mock terminal output", (await result.InvokeAsync(id, local, CancellationToken.None)).Content, StringComparison.Ordinal);
        Assert.DoesNotContain("mock terminal output", (await result.InvokeAsync(id, local with { BackendIsLocal = false }, CancellationToken.None)).Content,
            StringComparison.Ordinal);
        Assert.Contains("unknown_action", (await result.InvokeAsync(id, local with { ConversationId = Guid.NewGuid() }, CancellationToken.None)).Content,
            StringComparison.Ordinal);

        CapabilityRequest refused = capabilities.Propose(chat, "Open https://example.com", CapabilityService.BrowserOpen, "https://example.com");
        Assert.Equal("rejected", (await capabilities.ResolveAsync(refused.Id, false, CancellationToken.None))?.Status);
        Assert.Equal(1, executor.Invocations);
        Assert.Throws<ArgumentException>(() => capabilities.Propose(chat, "Read my notes", CapabilityService.Terminal, "env"));
        Assert.Throws<ArgumentException>(() => capabilities.Propose(chat, "Open a site", CapabilityService.BrowserOpen, "file:///etc/passwd"));
    }

    [Fact]
    public async Task MacTerminalCannotReadParentSecretsOrStartANetworkRequest()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        string marker = Path.Combine(root, "private-marker.txt");
        await File.WriteAllTextAsync(marker, "outside-workspace-secret");
        string command = $"printf 'inside' > file; cat file; if cat '{marker}'; then exit 3; fi; if printf bad > '{marker}'; then exit 4; fi; if /usr/bin/nc -z -w 1 127.0.0.1 1; then exit 5; fi; printf 'isolated'";
        CapabilityRequest request = new(Guid.NewGuid(), Guid.NewGuid(), CapabilityService.Terminal, command, "", "running", null, clock.GetUtcNow());
        CapabilityExecution result = await new LocalCapabilityExecutor().ExecuteAsync(request, CancellationToken.None);
        Assert.True(result.Succeeded, result.Detail);
        Assert.Contains("insideisolated", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("outside-workspace-secret", result.Detail, StringComparison.Ordinal);
        Assert.Equal("outside-workspace-secret", await File.ReadAllTextAsync(marker));
    }

    private sealed class RecordingCapabilityExecutor : ICapabilityExecutor
    {
        public int Invocations { get; private set; }

        public Task<CapabilityExecution> ExecuteAsync(CapabilityRequest request, CancellationToken cancellationToken)
        {
            Invocations++;
            Assert.Equal("printf marker", request.Target);
            return Task.FromResult(new CapabilityExecution(true, "mock terminal output"));
        }
    }
}
