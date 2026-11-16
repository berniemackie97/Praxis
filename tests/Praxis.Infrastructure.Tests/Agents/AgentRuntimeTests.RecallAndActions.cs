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
