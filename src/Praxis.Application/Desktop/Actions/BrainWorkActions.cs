using Praxis.Application.Agents;
using Praxis.Application.Agents.Memory;
using Praxis.Application.Processing;
using Praxis.Domain.Agents;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Authenticated first-party control of background learning and processor drain/resume.</summary>
public sealed class BrainWorkActions(AgentLearningReviewer learning, IAgentRunJournal journal, IProcessingControls controls,
    ProcessingPipeline pipeline) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("brainWork", _ => Snapshot()),
        DesktopAction.Of("brainLearningSet", fields =>
        {
            learning.Configure(new LearningSettings(fields.RequireString("mode", 16), fields.IntegerOrDefault("dailyCalls", 4, 1, 100),
                fields.IntegerOrDefault("dailyTokens", 24_000, 6_000, 600_000), fields.OptionalText("model", 100, "The learning model is invalid."),
                fields.OptionalBoolean("idleDream") ?? false, fields.IntegerOrDefault("dreamIntervalMinutes", 120, 30, 1_440)));
            return Snapshot();
        }),
        DesktopAction.Of("brainLearningReview", _ => new { status = learning.RequestManual() }),
        DesktopAction.Of("processorPause", fields =>
        {
            string processor = fields.RequireString("processorKey", 64);
            if (!pipeline.Processors.Contains(processor, StringComparer.Ordinal)) {throw new ArgumentException("Unknown processor.");
}            controls.SetPaused(processor, fields.RequireBoolean("paused")); pipeline.Wake();
            return Snapshot();
        })
    ];

    private object Snapshot() => new { settings = learning.Settings, processors = pipeline.Processors.Select(controls.Read), learning = journal.LearningHistory(30) };
}
