using System.Text;
using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Memory;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Agents.Tools;
using Praxis.Application.Inference;
using Praxis.Application.Policy;
using Praxis.Domain.Agents;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    [Fact]
    public async Task ChatMemoryResumesAfterRestartAndOnlyWritesGroundedUnduplicatedEntriesToExistingNotes()
    {
        const string quote = "We decided Orchid will list new coats on Fridays.";
        string file = Path.Combine(inbox, "Orchid Trading Company LLC.md");
        const string original = "# Orchid Trading Company LLC\n\nKeep this owner paragraph.\n";
        await File.WriteAllTextAsync(file, original);
        Guid chat;
        Guid source;
        using (SqliteCatalog first = SqliteCatalog.Open(root, clock))
        {
            await Capture(first, Path.GetFileName(file), Encoding.UTF8.GetBytes(original));
            source = first.Sources.FindLatestFile(file)!.SourceId;
            first.Models.Save(new ModelProfile(ModelProfile.LocalEnrichment, "ollama", "http://127.0.0.1:11434/", "gemma3:latest", 8_192, true));
            first.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.AllowEdits), PolicyActors.Owner);
            first.Policy.ApplyChange(new PolicyChange(Mode: DisclosureModes.AllowAll), PolicyActors.Owner);
            (AgentRuntime runtime, AgentConversation conversation) = Runtime(first, Local(),
                new ScriptedBackend(_ => new AgentTurn("Suggestion: move all existing inventory offline.", [], 1, 1)));
            await runtime.RunAsync(conversation.ConversationId, quote, (_, _, _) => { }, CancellationToken.None);
            chat = conversation.ConversationId;
            Assert.Null(first.ChatMemory.ReadRecap(chat));
        }

        clock.UtcNow += TimeSpan.FromSeconds(10);
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        WatchedFolderIntakeGroup intake = new(catalog.Sources, catalog.Originals, root, [inbox], clock: clock);
        intake.ApproveRoot(inbox);
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, new ScriptedModelFactory(), clock));
        ChatMemoryNotes notes = new(catalog.Sources, catalog.Search, catalog.Originals, catalog.Policy, intake, root, inbox);
        ChatMemoryMaintenance memory = new(catalog.ChatMemory, notes, catalog.Models,
            new MemoryFixtureModel(request =>
            {
                Assert.Contains("Orchid", request.Messages[1].Content, StringComparison.Ordinal);
                return JsonSerializer.Serialize(new
                {
                    recap = "The owner decided when Orchid will list coats and discussed other suggestions."
                });
            }), new PolicyService(catalog.Policy), catalog.Verification);

        Assert.True(await memory.RunNextAsync(CancellationToken.None));
        Assert.Contains("list coats", catalog.ChatMemory.ReadRecap(chat), StringComparison.Ordinal);
        string saved = await File.ReadAllTextAsync(file);
        Assert.StartsWith(original.TrimEnd(), saved, StringComparison.Ordinal);
        Assert.Contains(quote, saved, StringComparison.Ordinal);
        Assert.DoesNotContain("offline", saved, StringComparison.Ordinal);
        Assert.True(catalog.Policy.ReadCategory(source)?.LocalOnly);
        Assert.False(new DisclosureGate(catalog.Policy, clock).Decide(source, "codex", false, DisclosurePurposes.Chat).Decision.Allowed);
        Assert.Equal(2, catalog.Sources.CurrentFile(source)!.Revision);
        Assert.False(await memory.RunNextAsync(CancellationToken.None));

        catalog.Agents.Append(chat, AgentMessage.FromUser("We agreed that Orchid will list new coats on Fridays."));
        catalog.Agents.AppendFinal(chat, AgentMessage.FromAssistant("Understood.", []));
        clock.UtcNow += TimeSpan.FromSeconds(10);
        Assert.True(await memory.RunNextAsync(CancellationToken.None));
        Assert.Equal(saved, await File.ReadAllTextAsync(file));
        Assert.Equal(2, catalog.Sources.CurrentFile(source)!.Revision);
        Assert.True(catalog.Agents.Delete(chat));
        Assert.Null(catalog.ChatMemory.ReadRecap(chat));
        Assert.Equal(saved, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task ExplicitMemoryEditsAreLocalRevisionCheckedAndCanRenameTheAssistant()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.AllowEdits), PolicyActors.Owner);
        WatchedFolderIntakeGroup intake = new(catalog.Sources, catalog.Originals, root, [inbox], clock: clock);
        intake.ApproveRoot(inbox);
        string path = Path.Combine(inbox, "Orchid.md");
        const string initial = "# Orchid\n\nShip on Fridays.\n";
        await File.WriteAllTextAsync(path, initial);
        await Capture(catalog, "Orchid.md", Encoding.UTF8.GetBytes(initial));
        Guid id = catalog.Sources.FindLatestFile(path)!.SourceId;
        ChatMemoryNotes notes = new(catalog.Sources, catalog.Search, catalog.Originals, catalog.Policy, intake, root, inbox);
        ChatMemoryNote before = notes.Open(id, CancellationToken.None);
        MemoryEditTool edit = new(notes, catalog.ChatMemory, catalog.Agents);
        AgentToolContext context = new(Guid.NewGuid(), "ollama", true, ToolPermissions.AllowEdits, _ => true,
            "Update Orchid: change Ship on Fridays to Ship on Mondays.");
        using JsonDocument patch = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            action = "edit",
            sourceId = id,
            digest = before.Digest,
            previous = "Ship on Fridays.",
            replacement = "Ship on Mondays."
        }));

        AgentToolOutput denied = await edit.InvokeAsync(patch.RootElement, context with { BackendIsLocal = false }, CancellationToken.None);
        Assert.Contains("permission_denied", denied.Content, StringComparison.Ordinal);
        await File.AppendAllTextAsync(path, "\nAn external editor changed the note.\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => edit.InvokeAsync(patch.RootElement, context, CancellationToken.None));
        Assert.Contains("An external editor changed", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, initial);
        await edit.InvokeAsync(patch.RootElement, context, CancellationToken.None);
        Assert.Equal(initial.Replace("Fridays", "Mondays", StringComparison.Ordinal), await File.ReadAllTextAsync(path));

        var (runtime, chat) = Runtime(catalog, Local() with { BackendId = "ollama" }, new ScriptedBackend());
        AgentReply reply = await runtime.RunAsync(chat.ConversationId, "/name Lumen", (_, _, _) => { }, CancellationToken.None, useTools: false);
        Assert.Contains("Lumen", reply.Text, StringComparison.Ordinal);
        Assert.Equal("Lumen", catalog.ChatMemory.ReadName());
        using JsonDocument choice = JsonDocument.Parse("""{"action":"name","name":"Juniper"}""");
        await edit.InvokeAsync(choice.RootElement, context with { OwnerText = "Choose your own name." }, CancellationToken.None);
        Assert.Equal("Juniper", catalog.ChatMemory.ReadName());
    }

    private sealed class MemoryFixtureModel(Func<ChatRequest, string> answer) : IChatModelConnector, IChatModel
    {
        public ModelConnection Connect(ModelProfile profile) => new(this);

        public Task<ModelIdentity> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ModelIdentity("ollama", "fixture", "fixture-digest", null, null, null));

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ChatResult(answer(request), 10, 20, TimeSpan.Zero));
    }
}
