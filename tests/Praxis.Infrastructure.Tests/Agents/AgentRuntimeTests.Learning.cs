using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Memory;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Policy;
using Praxis.Domain.Agents;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    private const string LearnedSkill = """
        ---
        name: checked-validation
        description: Validate derived values with independent calculations and explicit stopping conditions.
        ---
        # Checked validation
        Use when a workflow requires independently computed values.
        1. Identify inputs and applicability before calculating.
        2. Use compute_evaluate for exact arithmetic.
        3. Compare independent results; stop if inputs or results disagree.
        Check the result against the original authorized evidence. Do not guess missing inputs.
        """;

    [Fact]
    public async Task LearningDraftRequiresRealReceiptsAndOwnerReviewAndIsRecalledAfterApproval()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentRunStatus completed = await LearningRun(catalog);
        int calls = 0;
        AgentLearningReviewer reviewer = LearningReviewer(catalog, request =>
        {
            calls++;
            Assert.Contains(completed.Reply!.ToolCalls[0].CallId, request.Messages[1].Content, StringComparison.Ordinal);
            return JsonSerializer.Serialize(new { action = "propose_skill", markdown = LearnedSkill, rationale = "Reusable checked calculation workflow.", evidenceCallIds = new[] { completed.Reply.ToolCalls[0].CallId } });
        });
        reviewer.Configure(new LearningSettings(LearningSettings.Manual));
        Assert.False(await reviewer.RunNextAsync(CancellationToken.None));
        reviewer.Configure(new LearningSettings(LearningSettings.AfterWork));
        Assert.True(await reviewer.RunNextAsync(CancellationToken.None));
        Assert.False(await reviewer.RunNextAsync(CancellationToken.None));
        Assert.Equal(1, calls);
        var draft = Assert.Single(catalog.Skills.History(null, true, 10));
        Assert.Empty(catalog.Skills.Installed());
        Assert.Equal("awaiting_owner_review", Assert.Single(catalog.AgentRuns.LearningHistory(10)).State);
        Assert.True(catalog.Skills.Resolve(draft.RevisionId, true));
        var skills = AgentComposition.Skills(catalog);
        Assert.Contains(skills.Search("checked validation", true, 10), skill => skill.Name == "checked-validation");
        Assert.True(catalog.Agents.Delete(completed.ConversationId));
        Assert.Null(catalog.Skills.ReadInstalled("checked-validation", true));
    }

    [Fact]
    public async Task MalformedLearningModelOutputReportsFailureWithoutCreatingASkill()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        await LearningRun(catalog);
        AgentLearningReviewer reviewer = LearningReviewer(catalog, _ => "No improvement to propose.");

        Assert.True(await reviewer.RunNextAsync(CancellationToken.None));
        AgentLearningReceipt receipt = Assert.Single(catalog.AgentRuns.LearningHistory(10));
        Assert.Equal("failed_invalid_json", receipt.State);
        Assert.Equal(10, receipt.InputTokens);
        Assert.Equal(20, receipt.OutputTokens);
        Assert.Empty(catalog.Skills.History(null, true, 10));
        Assert.False(await reviewer.RunNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InvalidLearningSkillProposalReportsFailureWithoutInstallingIt()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentRunStatus completed = await LearningRun(catalog);
        AgentLearningReviewer reviewer = LearningReviewer(catalog, _ => JsonSerializer.Serialize(new
        {
            action = "propose_skill",
            markdown = "This is not a skill document.",
            rationale = "A supposed reusable method.",
            evidenceCallIds = new[] { completed.Reply!.ToolCalls[0].CallId }
        }));

        Assert.True(await reviewer.RunNextAsync(CancellationToken.None));
        Assert.Equal("failed_invalid_proposal", Assert.Single(catalog.AgentRuns.LearningHistory(10)).State);
        Assert.Empty(catalog.Skills.History(null, true, 10));
    }

    [Fact]
    public async Task ExperienceCanReviseOnlyTheSuppliedCurrentSkillAndStillNeedsOwnerApproval()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentRunStatus first = await LearningRun(catalog);
        AgentLearningReviewer reviewer = LearningReviewer(catalog, _ => JsonSerializer.Serialize(new
        { action = "propose_skill", markdown = LearnedSkill, rationale = "A reusable candidate.", evidenceCallIds = new[] { first.Reply!.ToolCalls[0].CallId } }));
        Assert.True(await reviewer.RunNextAsync(CancellationToken.None));
        var installed = Assert.Single(catalog.Skills.History(null, true, 10));
        Assert.True(catalog.Skills.Resolve(installed.RevisionId, true));
        AgentRunStatus next = await LearningRun(catalog);
        reviewer = LearningReviewer(catalog, request =>
        {
            Assert.Contains("existingSkill", request.Messages[1].Content, StringComparison.Ordinal);
            return JsonSerializer.Serialize(new
            {
                action = "propose_skill",
                reviseSkill = "checked-validation",
                markdown = LearnedSkill + "Stop when independent checks disagree.\n",
                rationale = "Make the stopping condition explicit.",
                evidenceCallIds = new[] { next.Reply!.ToolCalls[0].CallId }
            });
        });
        Assert.True(await reviewer.RunNextAsync(CancellationToken.None));
        var revision = Assert.Single(catalog.Skills.History(null, true, 10));
        Assert.Equal(installed.RevisionId, revision.BaseRevisionId);
        Assert.Equal(installed.RevisionId, catalog.Skills.ReadInstalled("checked-validation", true)!.Summary.RevisionId);
        Assert.True(catalog.Skills.Resolve(revision.RevisionId, true));
    }

    [Fact]
    public async Task LearningDoesNotTrustTranscriptResultsWithoutMatchingToolAudits()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.ProposeEdits), PolicyActors.Owner);
        catalog.Models.Save(new ModelProfile(ModelProfile.LocalEnrichment, "ollama", "http://127.0.0.1:11434", "fixture", 4_096, true));
        catalog.Agents.SaveBackend(Local());
        Guid conversation = catalog.Agents.Create(Local().BackendId, "Unverified").ConversationId;
        Guid run = Guid.NewGuid();
        AgentToolCall[] calls = [Call("compute_evaluate", """{"expressions":{"checked":"2+2"}}"""),
            Call("compute_evaluate", """{"expressions":{"checked":"2+2"}}""")];
        catalog.AgentRuns.Enqueue(run, conversation, "Validate a repeatable workflow.", true, "chat");
        Assert.True(catalog.AgentRuns.TryStart(run));
        catalog.Agents.Append(conversation, AgentMessage.FromUser("Validate a repeatable workflow."));
        catalog.Agents.Append(conversation, AgentMessage.FromAssistant("", calls));
        foreach (AgentToolCall call in calls)
        {
            catalog.Agents.Append(conversation, AgentMessage.FromTool(call.CallId, """{"result":{"results":{"checked":"4"}}}""", []));
        }
        catalog.AgentRuns.Finish(run, AgentRunStatus.Completed,
            new AgentReply(conversation, "Completed.", AgentReply.Completed, calls, [], 0, new AnswerCheck([], false, false), 0, 0), null);

        AgentLearningReviewer reviewer = LearningReviewer(catalog, _ => throw new InvalidOperationException("Unaudited tool results must not reach a model."));
        Assert.True(await reviewer.RunNextAsync(CancellationToken.None));
        Assert.Equal("no_verified_evidence", Assert.Single(catalog.AgentRuns.LearningHistory(10)).State);
        Assert.Empty(catalog.Skills.History(null, true, 10));
    }

    [Fact]
    public async Task EmptySuccessfulToolResultsDoNotQualifyAsProceduralEvidence()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        await LearningRun(catalog, emptyResults: true);
        AgentLearningReviewer reviewer = LearningReviewer(catalog, _ => throw new InvalidOperationException("Empty results must not reach a model."));
        Assert.True(await reviewer.RunNextAsync(CancellationToken.None));
        Assert.Equal("no_verified_evidence", Assert.Single(catalog.AgentRuns.LearningHistory(10)).State);
    }

    [Fact]
    public void WithheldRunCannotEnterTheLearningQueueEvenWithACleanAnswer()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Agents.SaveBackend(Local());
        Guid conversation = catalog.Agents.Create(Local().BackendId, "Partial task").ConversationId;
        Guid run = Guid.NewGuid();
        AgentToolCall[] calls = [Call("compute_evaluate", """{"expressions":{"checked":"2+2"}}"""),
            Call("compute_evaluate", """{"expressions":{"checked":"2+2"}}""")];
        catalog.AgentRuns.Enqueue(run, conversation, "Validate a repeatable workflow.", true, "chat");
        Assert.True(catalog.AgentRuns.TryStart(run));
        catalog.AgentRuns.Finish(run, AgentRunStatus.Completed,
            new AgentReply(conversation, "Completed.", AgentReply.Completed, calls, [], 1, new AnswerCheck([], false, false), 0, 0), null);
        Assert.Empty(catalog.AgentRuns.LearningHistory(10));
    }

    [Fact]
    public async Task FailedToolReceiptsCannotBecomeLearningEvidence()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        await LearningRun(catalog, invalidTools: true);
        AgentLearningReviewer reviewer = LearningReviewer(catalog, _ => throw new InvalidOperationException("No inference expected."));
        reviewer.Configure(new LearningSettings(LearningSettings.AfterWork));
        Assert.True(await reviewer.RunNextAsync(CancellationToken.None));
        Assert.Equal("no_verified_evidence", Assert.Single(catalog.AgentRuns.LearningHistory(10)).State);
        Assert.Empty(catalog.Skills.History(null, true, 10));
    }

    [Fact]
    public async Task LearningConsentChangeDuringInferenceDiscardsItsDraft()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentRunStatus completed = await LearningRun(catalog);
        AgentLearningReviewer? reviewer = null;
        reviewer = LearningReviewer(catalog, _ =>
        {
            reviewer!.Configure(new LearningSettings(LearningSettings.Off));
            return JsonSerializer.Serialize(new { action = "propose_skill", markdown = LearnedSkill, rationale = "A candidate only.", evidenceCallIds = new[] { completed.Reply!.ToolCalls[0].CallId } });
        });
        reviewer.Configure(new LearningSettings(LearningSettings.AfterWork));
        Assert.True(await reviewer.RunNextAsync(CancellationToken.None));
        Assert.Empty(catalog.Skills.History(null, true, 10));
        Assert.Equal("invalidated", Assert.Single(catalog.AgentRuns.LearningHistory(10)).State);
    }

    [Fact]
    public async Task ManualReviewRequestReportsQueueStateAndRunsWhenWorkIsAvailable()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.ProposeEdits), PolicyActors.Owner);
        catalog.Models.Save(new ModelProfile(ModelProfile.LocalEnrichment, "ollama", "http://127.0.0.1:11434", "fixture", 4_096, true));
        AgentLearningReviewer reviewer = LearningReviewer(catalog, _ => """{"action":"no_change","evidenceCallIds":[]}""");
        reviewer.Configure(new LearningSettings(LearningSettings.Manual));

        Assert.Equal("no_pending_work", reviewer.RequestManual());
        await LearningRun(catalog);
        Assert.Equal("queued", reviewer.RequestManual());
        Assert.Equal("queued", reviewer.RequestManual());
        Assert.True(await reviewer.RunNextAsync(CancellationToken.None));
        Assert.Equal("no_change", Assert.Single(catalog.AgentRuns.LearningHistory(10)).State);
        Assert.Equal("no_pending_work", reviewer.RequestManual());

        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.ReadOnly), PolicyActors.Owner);
        Assert.Equal("permission_required", reviewer.RequestManual());
        reviewer.Configure(new LearningSettings(LearningSettings.Off));
        Assert.Equal("off", reviewer.RequestManual());
    }

    [Fact]
    public async Task ManualReviewRequestReportsAnActiveReviewBeforeBudgetExhaustion()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        await LearningRun(catalog);
        AgentLearningReviewer reviewer = LearningReviewer(catalog, _ => """{"action":"no_change","evidenceCallIds":[]}""");
        reviewer.Configure(new LearningSettings(LearningSettings.Manual, 1, 6_000));

        Assert.NotNull(catalog.AgentRuns.ClaimLearning(1, 6_000));
        Assert.Equal("review_running", reviewer.RequestManual());
    }

    [Fact]
    public async Task ManualReviewRequestReportsDailyCallAndTokenLimitsWithoutClaimingWork()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        await LearningRun(catalog);
        AgentLearningReviewer reviewer = LearningReviewer(catalog, _ => """{"action":"no_change","evidenceCallIds":[]}""");
        reviewer.Configure(new LearningSettings(LearningSettings.Manual, 1, 6_000));

        Assert.Equal("queued", reviewer.RequestManual());
        Assert.True(await reviewer.RunNextAsync(CancellationToken.None));
        await LearningRun(catalog);
        Assert.Equal("daily_call_limit", reviewer.RequestManual());
        reviewer.Configure(new LearningSettings(LearningSettings.Manual, 4, 6_000));
        Assert.Equal("daily_token_limit", reviewer.RequestManual());
        Assert.Equal("pending", Assert.Single(catalog.AgentRuns.LearningHistory(10).Where(item => item.State == "pending")).State);
    }

    [Fact]
    public async Task LearningBudgetCountsRestartedAttemptsAndEmptyWorkDoesNotCallTheModel()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        await LearningRun(catalog);
        Assert.NotNull(catalog.AgentRuns.ClaimLearning(4, 6_000));
        catalog.AgentRuns.RecoverInterrupted();
        Assert.Null(catalog.AgentRuns.ClaimLearning(4, 6_000));
        Assert.NotNull(catalog.AgentRuns.ClaimLearning(4, 12_000));
        catalog.AgentRuns.RecoverInterrupted();
        Assert.Null(catalog.AgentRuns.ClaimLearning(100, 600_000));
        Assert.Equal("failed", Assert.Single(catalog.AgentRuns.LearningHistory(10)).State);
    }

    [Fact]
    public void InterruptedDreamResumesWithABoundedAttemptCountButOrdinaryChatNeverReplays()
    {
        Guid dream = Guid.NewGuid(), chat = Guid.NewGuid();
        using (SqliteCatalog catalog = SqliteCatalog.Open(root, clock))
        {
            catalog.Agents.SaveBackend(Local());
            Guid conversation = catalog.Agents.Create(Local().BackendId, "Recovery").ConversationId;
            catalog.AgentRuns.Enqueue(dream, conversation, "/dream inspect aliases", true, "dream");
            catalog.AgentRuns.Enqueue(chat, conversation, "An ordinary request", true, "chat");
            Assert.True(catalog.AgentRuns.TryStart(dream)); Assert.True(catalog.AgentRuns.TryStart(chat));
            catalog.AgentRuns.AppendEvent(dream, "checkpoint", "knowledge_search", "read completed");
        }
        using SqliteCatalog reopened = SqliteCatalog.Open(root, clock);
        reopened.AgentRuns.RecoverInterrupted();
        Assert.Equal(dream, Assert.Single(reopened.AgentRuns.Pending(10)).RunId);
        Assert.Equal(AgentRunStatus.Failed, reopened.AgentRuns.Status(chat, 0)!.State);
        Assert.Single(reopened.AgentRuns.Status(dream, 0)!.Events);
        Assert.True(reopened.AgentRuns.TryStart(dream)); reopened.AgentRuns.Suspend(dream);
        Assert.True(reopened.AgentRuns.TryStart(dream)); reopened.AgentRuns.RecoverInterrupted();
        Assert.Empty(reopened.AgentRuns.Pending(10));
        Assert.False(reopened.AgentRuns.HasActiveDream());
    }

    [Fact]
    public async Task DurableDreamCompletionAndCanceledPendingWorkSurviveCoordinatorReplacement()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        ScriptedBackend backend = new(_ => new AgentTurn("No supported change.", [], 1, 1)) { ExpectedTool = "knowledge_search" };
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        Guid canceled = Guid.NewGuid(), restored = Guid.NewGuid();
        catalog.AgentRuns.Enqueue(canceled, conversation.ConversationId, "/dream inspect aliases", true, "dream");
        Assert.True(catalog.AgentRuns.CancelPending(canceled));
        catalog.AgentRuns.Enqueue(restored, conversation.ConversationId, "/dream inspect aliases", true, "dream");
        AgentRunCoordinator coordinator = new(runtime, clock, catalog.AgentRuns);
        coordinator.RestorePending();
        AgentRunStatus completed = await Poll(coordinator, restored);
        Assert.Equal(AgentRunStatus.Completed, completed.State);
        AgentRunCoordinator replacement = new(runtime, clock, catalog.AgentRuns);
        Assert.Equal(completed.Reply!.Text, replacement.Status(restored, 0)!.Reply!.Text);
        Assert.Equal(AgentRunStatus.Canceled, replacement.Status(canceled, 0)!.State);
    }

    [Fact]
    public async Task IdleLearningWaitsForQuietTimeWithoutConsumingABudgetAttempt()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        await LearningRun(catalog);
        AgentLearningReviewer reviewer = LearningReviewer(catalog, _ => """{"action":"no_change","evidenceCallIds":[]}""");
        reviewer.Configure(new LearningSettings(LearningSettings.Idle));
        Assert.False(await reviewer.RunNextAsync(CancellationToken.None));
        Assert.Equal("pending", Assert.Single(catalog.AgentRuns.LearningHistory(10)).State);
        clock.UtcNow += TimeSpan.FromMinutes(6);
        Assert.True(await reviewer.RunNextAsync(CancellationToken.None));
        Assert.Equal("no_change", Assert.Single(catalog.AgentRuns.LearningHistory(10)).State);
    }

    [Fact]
    public async Task RevokedToolEvidenceInvalidatesLearningEvenWhenFinalAnswerHasNoCitations()
    {
        using SqliteCatalog catalog = await SeededAsync();
        var source = catalog.Library.List(10, 0, null).First();
        catalog.Agents.SaveBackend(Local());
        Guid conversation = catalog.Agents.Create(Local().BackendId, "Evidence scope").ConversationId;
        Guid run = Guid.NewGuid();
        catalog.AgentRuns.Enqueue(run, conversation, "Validate the procedure.", true, "chat");
        Assert.True(catalog.AgentRuns.TryStart(run));
        catalog.Agents.Append(conversation, AgentMessage.FromTool("observed", "{}",
            [new AgentCitation(source.SourceId, source.ItemId, source.Revision, "test", "source")]));
        catalog.AgentRuns.Finish(run, AgentRunStatus.Completed,
            new AgentReply(conversation, "Done.", AgentReply.Completed, [], [], 0, new AnswerCheck([], false, false), 0, 0), null);
        Assert.True(catalog.AgentRuns.EvidenceCurrent(run));
        Assert.True(catalog.Sources.Revoke(source.SourceId));
        Assert.False(catalog.AgentRuns.EvidenceCurrent(run));
    }

    [Fact]
    public void QueuedRunRejectsChangedModelAndUnknownConversationBeforeExecution()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Agents.SaveBackend(Local());
        Guid conversation = catalog.Agents.Create(Local().BackendId, "Pinned model").ConversationId;
        Guid run = Guid.NewGuid();
        catalog.AgentRuns.Enqueue(run, conversation, "/dream inspect aliases", true, "dream");
        catalog.Agents.SetModel(conversation, "different", null);
        Assert.False(catalog.AgentRuns.TryStart(run));
        Guid endpointRun = Guid.NewGuid();
        catalog.AgentRuns.Enqueue(endpointRun, conversation, "/dream inspect aliases", true, "dream");
        catalog.Agents.SaveBackend(Local() with { Endpoint = "http://127.0.0.1:11435/v1" });
        Assert.False(catalog.AgentRuns.TryStart(endpointRun));
        Assert.Throws<ArgumentException>(() => catalog.AgentRuns.Enqueue(Guid.NewGuid(), Guid.NewGuid(), "request", true, "chat"));
    }

    private async Task<AgentRunStatus> LearningRun(SqliteCatalog catalog, bool invalidTools = false, bool emptyResults = false)
    {
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.ProposeEdits), PolicyActors.Owner);
        catalog.Models.Save(new ModelProfile(ModelProfile.LocalEnrichment, "ollama", "http://127.0.0.1:11434", "fixture", 4_096, true));
        string args = invalidTools ? "{}" : emptyResults ? """{"expressions":{}}""" : """{"expressions":{"checked":"2+2"}}""";
        ScriptedBackend backend = new(_ => Turn(Call("compute_evaluate", args), Call("compute_evaluate", args)),
            _ => new AgentTurn("The checked workflow completed.", [], 1, 1));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        AgentRunCoordinator coordinator = new(runtime, clock, catalog.AgentRuns);
        AgentRunStatus status = await Poll(coordinator, coordinator.Start(conversation.ConversationId, "Validate a repeatable derived-value workflow."));
        Assert.Equal(AgentRunStatus.Completed, status.State);
        return status;
    }

    private AgentLearningReviewer LearningReviewer(SqliteCatalog catalog, Func<Praxis.Application.Inference.ChatRequest, string> answer) =>
        new(catalog.AgentRuns, catalog.Settings, catalog.Models, catalog.Verification, new MemoryFixtureModel(answer), AgentComposition.Skills(catalog),
            new PolicyService(catalog.Policy), clock, new ModelActivity());
}
