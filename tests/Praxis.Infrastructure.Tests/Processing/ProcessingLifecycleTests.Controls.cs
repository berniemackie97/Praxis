using Praxis.Domain.Agents;
using Praxis.Domain.Processing;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Processing;

public sealed partial class ProcessingLifecycleTests
{
    [Fact]
    public async Task ProcessorPauseDrainsAndSurvivesRestartWithoutDroppingQueuedWork()
    {
        using (SqliteCatalog catalog = SqliteCatalog.Open(root))
        {
            using MemoryStream bytes = new([1]);
            await catalog.Originals.CaptureFileAsync(Source("paused"), bytes, 1, "paused");
            ProcessingLease lease = Assert.IsType<ProcessingLease>(catalog.Processing.TryClaim(TimeSpan.FromMinutes(1), processorKey: ProcessorKeys.SourceText));
            catalog.ProcessingControls.SetPaused(ProcessorKeys.SourceText, true);
            Assert.Equal("draining", catalog.ProcessingControls.Read(ProcessorKeys.SourceText).State);
            Assert.True(catalog.Processing.Defer(lease));
            Assert.Null(catalog.Processing.TryClaim(TimeSpan.FromMinutes(1), processorKey: ProcessorKeys.SourceText));
        }
        using SqliteCatalog reopened = SqliteCatalog.Open(root);
        Assert.Equal("paused", reopened.ProcessingControls.Read(ProcessorKeys.SourceText).State);
        Assert.Equal(1, reopened.ProcessingControls.Read(ProcessorKeys.SourceText).Waiting);
        reopened.ProcessingControls.SetPaused(ProcessorKeys.SourceText, false);
        Assert.NotNull(reopened.Processing.TryClaim(TimeSpan.FromMinutes(1), processorKey: ProcessorKeys.SourceText));
    }

    [Fact]
    public async Task DreamYieldsIntelligenceLeasesWhileCaptureCanContinueAndRevocationStillWins()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        using MemoryStream bytes = new([1]);
        var source = await catalog.Originals.CaptureFileAsync(Source("dream"), bytes, 1, "dream");
        catalog.Processing.Queue(source.ItemId, source.Revision, ProcessorKeys.IntakeTriage);
        catalog.Agents.SaveBackend(new AgentBackendProfile("local", AgentBackendKinds.OpenAiCompatible, "http://127.0.0.1:11434/v1", "fixture", null, null, true));
        Guid conversation = catalog.Agents.Create("local", "Dream").ConversationId, run = Guid.NewGuid();
        catalog.AgentRuns.Enqueue(run, conversation, "/dream", true, "dream");
        Assert.True(catalog.AgentRuns.TryStart(run));
        Assert.Null(catalog.Processing.TryClaim(TimeSpan.FromMinutes(1), processorKey: ProcessorKeys.IntakeTriage));
        Assert.NotNull(catalog.Processing.TryClaim(TimeSpan.FromMinutes(1), processorKey: ProcessorKeys.SourceText));
        Assert.Equal("yielding_to_dream", catalog.ProcessingControls.Read(ProcessorKeys.IntakeTriage).State);
        catalog.AgentRuns.Finish(run, AgentRunStatus.Completed, null, null);
        Assert.True(catalog.Sources.Revoke(source.SourceId));
        Assert.Null(catalog.Processing.TryClaim(TimeSpan.FromMinutes(1), processorKey: ProcessorKeys.IntakeTriage));
    }
}
