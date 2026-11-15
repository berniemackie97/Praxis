using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Agents.Skills;
using Praxis.Application.Agents.Tools;
using Praxis.Application.Desktop;
using Praxis.Application.Desktop.Actions;
using Praxis.Application.Policy;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Agents;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    private static readonly YamlSkillDocumentParser SkillParser = new();
    private const string TrainingBody = "Validate the violet inventory marker before reporting completion.";

    [Fact]
    public async Task ChatCanLearnThenDiscoverAndOpenAnOwnerApprovedSkillInAnotherConversation()
    {
        Guid id;
        using (SqliteCatalog catalog = SqliteCatalog.Open(root, clock))
        {
            new PolicyService(catalog.Policy).Request(new PolicyChange(ToolPermission: ToolPermissions.AllowEdits), PolicyActors.Owner);
            ScriptedBackend teacher = new(
                _ => Turn(Call("skill_propose", ToolJson.Serialize(new { markdown = SkillMarkdown(TrainingBody), rationale = "Owner explained a verified process." }))),
                transcript =>
                {
                    Assert.Contains("awaiting_owner_review", transcript[^1].Content, StringComparison.Ordinal);
                    return new AgentTurn("The draft is ready for review.", [], 1, 1);
                });
            (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), teacher);
            await runtime.RunAsync(conversation.ConversationId, "Learn this repeatable workflow.", (_, _, _) => { }, CancellationToken.None);

            SkillLibrary library = AgentComposition.Skills(catalog);
            Assert.Null(library.Open("violet-inventory", true, true));
            SkillRevisionSummary draft = Assert.Single(library.History(null, pendingOnly: true, limit: 10));
            Assert.Equal(conversation.ConversationId, draft.ConversationId);
            Assert.Equal("{}", Assert.Single(catalog.Agents.ReadToolCalls(conversation.ConversationId, 10)).ArgumentsJson);
            id = draft.RevisionId;

            DesktopProtocol desktop = new([new SkillActions(library)]);
            using JsonDocument review = JsonDocument.Parse(await desktop.HandleAsync(ToolJson.Serialize(
                new { action = "skillRevisionRead", requestId = "review", revisionId = id })));
            Assert.Contains(TrainingBody, review.RootElement.GetProperty("data").GetProperty("markdown").GetString(), StringComparison.Ordinal);
            using JsonDocument approve = JsonDocument.Parse(await desktop.HandleAsync(ToolJson.Serialize(
                new { action = "skillDraftResolve", requestId = "approve", revisionId = id, accept = true })));
            Assert.True(approve.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        }

        using SqliteCatalog reopened = SqliteCatalog.Open(root, clock);
        ScriptedBackend worker = new(
            _ => Turn(Call("skill_open", """{"name":"violet-inventory"}""")),
            transcript =>
            {
                Assert.Contains(TrainingBody, transcript[^1].Content, StringComparison.Ordinal);
                Assert.Contains(id.ToString("D"), transcript[^1].Content, StringComparison.Ordinal);
                return new AgentTurn("I checked the inventory marker.", [], 1, 1);
            },
            transcript =>
            {
                Assert.DoesNotContain(transcript, item => item.Content.Contains(TrainingBody, StringComparison.Ordinal));
                return new AgentTurn("That procedure is no longer available.", [], 1, 1);
            });
        (AgentRuntime assistant, AgentConversation second) = Runtime(reopened, Local(), worker);
        await assistant.RunAsync(second.ConversationId, "Use $violet-inventory please.", (_, _, _) => { }, CancellationToken.None);
        Assert.Contains("Check unfamiliar inventory.", worker.SystemPrompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain(TrainingBody, worker.SystemPrompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain(reopened.Agents.ReadMessages(second.ConversationId),
            message => message.Content.Contains(TrainingBody, StringComparison.Ordinal));
        Assert.Contains(reopened.Agents.ReadMessages(second.ConversationId), message => message.Role == AgentRoles.Tool &&
            message.Content.Contains(id.ToString("D"), StringComparison.Ordinal));
        Assert.True(AgentComposition.Skills(reopened).SetEnabled("violet-inventory", false));
        await assistant.RunAsync(second.ConversationId, "Use $violet-inventory again.", (_, _, _) => { }, CancellationToken.None);
        Assert.DoesNotContain("Check unfamiliar inventory.", worker.SystemPrompts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistryDeniesRemotePrivateSkillsAndRefusesProposedToolPermissions()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        SkillLibrary library = AgentComposition.Skills(catalog);
        SkillRevisionSummary draft = library.Propose(SkillMarkdown(TrainingBody), null, "Imported.");
        Assert.True(library.Resolve(draft.RevisionId, accept: true));

        AgentToolRegistry registry = new(Tools(catalog), catalog.Agents, clock);
        AgentToolContext remote = new(Guid.NewGuid(), "remote", false, ToolPermissions.AllowEdits, _ => false);
        AgentToolContext readOnly = remote with { BackendIsLocal = true, ToolPermission = ToolPermissions.ReadOnly };
        AgentToolContext local = readOnly with { ToolPermission = ToolPermissions.AllowEdits };
        Assert.Contains("skill_unavailable", (await registry.InvokeAsync(Call("skill_open", """{"name":"violet-inventory"}"""),
            remote, CancellationToken.None)).Content, StringComparison.Ordinal);
        Assert.DoesNotContain("violet-inventory", (await registry.InvokeAsync(Call("skill_search", """{"query":"violet"}"""),
            remote, CancellationToken.None)).Content, StringComparison.Ordinal);
        Assert.Contains("github-project-reconcile", (await registry.InvokeAsync(Call("skill_open", """{"name":"github-project-reconcile"}"""),
            remote, CancellationToken.None)).Content, StringComparison.Ordinal);

        string edit = ToolJson.Serialize(new { markdown = SkillMarkdown("Try a second marker."), rationale = "Trial." });
        Assert.Contains("local_only", (await registry.InvokeAsync(Call("skill_propose", edit), remote, CancellationToken.None)).Content, StringComparison.Ordinal);
        Assert.Contains("permission_denied", (await registry.InvokeAsync(Call("skill_propose", edit), readOnly, CancellationToken.None)).Content, StringComparison.Ordinal);
        Assert.Contains("permission_denied", (await registry.InvokeAsync(Call("system_run_command", """{"command":"touch local-marker"}"""),
            local, CancellationToken.None)).Content, StringComparison.Ordinal);
        Assert.Empty(library.History(null, pendingOnly: true, limit: 10));
    }

    [Fact]
    public void OwnerCanRejectDraftsAndStaleApprovalCannotReplaceANewerPinnedRevision()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        SkillLibrary library = AgentComposition.Skills(catalog);
        SkillRevisionSummary v1 = library.Propose(SkillMarkdown("Check the first marker."), null, "Example.");
        Assert.True(library.Resolve(v1.RevisionId, true));

        Guid conversation = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => library.Propose(SkillMarkdown("Wrong base."), conversation, "Stale."));
        SkillRevisionSummary rejected = library.Propose(SkillMarkdown("Check the second marker."), null, "Alternative.");
        Assert.True(library.Resolve(rejected.RevisionId, false));
        Assert.False(library.Resolve(rejected.RevisionId, true));
        SkillRevisionSummary v2 = library.Propose(SkillMarkdown("Check the revised marker."), conversation, "Verified.", v1.RevisionId);
        SkillRevisionSummary alt = library.Propose(SkillMarkdown("Check the other marker."), conversation, "Alternative.", v1.RevisionId);
        Assert.True(library.SetEnabled("violet-inventory", false));
        Assert.True(library.Resolve(v2.RevisionId, true));
        Assert.Throws<InvalidOperationException>(() => library.Resolve(alt.RevisionId, true));
        Assert.Equal("pending", library.ReadRevision(alt.RevisionId)?.Summary.State);
        Assert.Null(library.Open("violet-inventory", true, true));
        Assert.True(library.SetEnabled("violet-inventory", true));
        Assert.Equal(v2.Digest, library.Open("violet-inventory", true, true)?.Digest);
        Assert.Equal("Check the first marker.", SkillParser.Parse(library.ReadRevision(v1.RevisionId)!.Markdown).Instructions);
    }

    [Fact]
    public void AnExistingConnectionCannotMoveItsPrivateConversationToARemoteEndpoint()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentBackendProfile local = Local();
        catalog.Agents.SaveBackend(local);
        catalog.Agents.Create(local.BackendId, "Private");
        Assert.Throws<ArgumentException>(() => catalog.Agents.SaveBackend(local with { Endpoint = "https://example.invalid/v1" }));
        Assert.True(Assert.Single(catalog.Agents.ReadBackends()).IsLocal);
    }

    [Fact]
    public void PortableMarkdownRoundTripsWithoutInterpretingToolGrants()
    {
        const string markdown = """
            ---
            name: violet-inventory
            description: >
              Check unfamiliar
              inventory.
            allowed-tools: Bash
            metadata:
              author: local
            ---
            Check the marker.
            """;
        SkillDocument document = SkillParser.Parse(markdown);
        Assert.Equal("Check unfamiliar inventory.", document.Description);
        Assert.Equal(markdown, document.Markdown);
        Assert.Equal("Check the marker.", document.Instructions);
        Assert.Throws<ArgumentException>(() => SkillParser.Parse(markdown.Replace("violet-inventory", "../secret", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => SkillParser.Parse("---\nname: &name violet-inventory\ndescription: *name\n---\nbody"));
        Assert.Throws<ArgumentException>(() => SkillParser.Parse("---\nname: first\nname: second\ndescription: Check evidence.\n---\nbody"));
    }

    private static string SkillMarkdown(string body) =>
        $"---\nname: violet-inventory\ndescription: Check unfamiliar inventory.\n---\n\n{body}\n";
}
