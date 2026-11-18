using System.Text;
using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Runtime;
using Praxis.Domain.Agents;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    [Theory]
    [InlineData("Both calculations completed successfully.")]
    [InlineData("The results were 391 and 420.")]
    public async Task SelfContainedCalculatorWorkflowDoesNotRequireUnrelatedBrainCitations(string finalText)
    {
        using SqliteCatalog catalog = await SeededAsync();
        await Capture(catalog, "calculation-workflow.md", Encoding.UTF8.GetBytes(
            "Perform a two-step calculation workflow using the compute_evaluate tool. First calculate 17 * 23, then calculate 391 + 29."));
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.NotEmpty(catalog.Search.Query("calculation workflow", 10));

        const string prompt = """
            Perform a two-step calculation workflow using the compute_evaluate tool.
            First call the tool to calculate 17 * 23.
            After that tool call finishes, make a separate second tool call to calculate 391 + 29.
            If both succeeded, give a concise completion report with or without the computed figures.
            Do not cite unrelated Brain material.
            """;
        ScriptedBackend backend = new(
            _ => Turn(Call("compute_evaluate", """{"expressions":{"first":"17 * 23"}}""")),
            _ => Turn(Call("compute_evaluate", """{"expressions":{"second":"391 + 29"}}""")),
            _ => new AgentTurn(finalText, [], 10, 5));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);

        AgentRunCoordinator coordinator = new(runtime, clock, catalog.AgentRuns);
        AgentRunStatus finished = await Poll(coordinator, coordinator.Start(conversation.ConversationId, prompt));
        Assert.True(finished.State == AgentRunStatus.Completed,
            string.Join("; ", finished.Events.Select(item => $"{item.Kind}: {item.Detail}")));
        AgentReply answer = Assert.IsType<AgentReply>(finished.Reply);

        Assert.Equal(AgentReply.Completed, answer.StopReason);
        Assert.Equal(finalText, answer.Text);
        Assert.True(answer.Check.Clean);
        Assert.Empty(answer.Citations);
        Assert.Equal(2, answer.ToolCalls.Count);
        IReadOnlyList<AgentMessage> transcript = catalog.Agents.ReadMessages(conversation.ConversationId);
        Assert.DoesNotContain(transcript, message => message.Content.Contains("Local library search excerpts", StringComparison.Ordinal));
        AgentMessage[] receipts = transcript.Where(message => message.Role == AgentRoles.Tool).ToArray();
        Assert.Equal(2, receipts.Length);
        Assert.Equal("391", CalculatorResult(receipts[0], "first"));
        Assert.Equal("420", CalculatorResult(receipts[1], "second"));
        Assert.All(catalog.Agents.ReadToolCalls(conversation.ConversationId, 10),
            audit => Assert.Equal("ok", audit.Outcome));
        Assert.Equal("not_needed", Assert.Single(catalog.AgentRecalls.Read(conversation.ConversationId, 10)).Outcome);
        Assert.Equal("pending", Assert.Single(catalog.AgentRuns.LearningHistory(10)).State);
    }

    [Fact]
    public async Task NumericToolResultsSupportTwoFiguresWithoutSourceCitations()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        ScriptedBackend backend = new(
            _ => Turn(Call("compute_evaluate", """{"expressions":{"first":"17 * 23"}}""")),
            _ => Turn(Call("compute_evaluate", """{"expressions":{"second":"391 + 29"}}""")),
            _ => new AgentTurn("The results were 391 and 420.", [], 10, 5));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);

        AgentReply answer = await runtime.RunAsync(conversation.ConversationId,
            "Using compute_evaluate, calculate 17 * 23 and 391 + 29.", (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(AgentReply.Completed, answer.StopReason);
        Assert.True(answer.Check.Clean);
        Assert.Empty(answer.Check.UnsupportedFigures);
        Assert.Equal(2, answer.ToolCalls.Count);
    }

    [Theory]
    [InlineData("Using compute_evaluate, explain the procedure in my calculation-workflow.md for 17 * 23.")]
    [InlineData("Use compute_evaluate to calculate 17 * 23 based on the source document calculation-workflow.md.")]
    public async Task SourceDependentCalculationStillRequiresTheSourceCitation(string prompt)
    {
        using SqliteCatalog catalog = await SeededAsync();
        await Capture(catalog, "calculation-workflow.md", Encoding.UTF8.GetBytes(
            "Source document calculation-workflow.md. Use compute_evaluate to calculate 17 * 23 based on the source document calculation-workflow.md. Using compute_evaluate, explain the procedure in my calculation-workflow.md: multiply 17 by 23, then add 29."));
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        AgentBackendProfile profile = Local() with { BackendId = "ollama" };
        catalog.Agents.SaveBackend(profile);
        AgentConversation chat = catalog.Agents.Create(profile.BackendId, "source dependent calculation");
        ScriptedBackend backend = new(
            _ => new AgentTurn("The source describes multiplication followed by addition.", [], 10, 5),
            _ => new AgentTurn("The source describes multiplication followed by addition.", [], 10, 5));
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(backend, null), clock);

        AgentReply answer = await runtime.RunAsync(chat.ConversationId,
            prompt, (_, _, _) => { }, CancellationToken.None);

        AgentRecallReceipt recalled = Assert.Single(catalog.AgentRecalls.Read(chat.ConversationId, 10));
        Assert.NotEmpty(recalled.Selected);
        Assert.Equal(AgentReply.EvidenceInsufficient, answer.StopReason);
        Assert.True(answer.Check.MissingCitations);
        Assert.NotEmpty(answer.Citations);
        Assert.NotEqual("not_needed", recalled.Outcome);
    }

    private static string CalculatorResult(AgentMessage message, string label)
    {
        using JsonDocument receipt = JsonDocument.Parse(message.Content);
        return receipt.RootElement.GetProperty("result").GetProperty("results").GetProperty(label).GetString()!;
    }
}
