using Praxis.Application.Models;
using Praxis.Application.Policy;
using Praxis.Domain.Models;
using Xunit;

namespace Praxis.Infrastructure.Tests.Models;

/// <summary>Local setup that fits the machine, and drafts asked for only when they pay off.</summary>
public sealed class MachineTuningTests
{
    private const long GiB = 1L << 30;

    // The owner's installed models at the time of the audit.
    private static readonly InstalledModel[] Installed =
    [
        new("hf.co/mradermacher/Gemma-4-E4B-IT-ABLITERATED-UNCENSORED-PHILADELPHIA-CLASS-i1-GGUF:IQ4_XS", "a", 5_000_000_000),
        new("embeddinggemma-2:latest", "b", 1_300_000_000),
        new("gemma2:latest", "c", 5_400_000_000),
        new("gemma3:latest", "d", 3_400_000_000),
        new("qwen3-embedding:latest", "e", 4_700_000_000),
        new("embeddinggemma:latest", "f", 600_000_000),
        new("huihui-qwen3.5:9b-q4_K_M", "g", 5_600_000_000)
    ];

    [Fact]
    public void ASixteenGigabyteMacKeepsOneSmallModelLoadedAndDraftsOnlyWhenMeasuredToPay()
    {
        MachinePlan plan = MachineTuning.Plan(16 * GiB, Installed);

        Assert.Equal((MachinePlan.Efficient, "gemma3:latest", "embeddinggemma:latest"), (plan.Profile, plan.LocalModel, plan.EmbeddingModel));
        Assert.Equal((LocalDraftModes.Auto, 20, false), (plan.DraftMode, plan.DraftBudgetSeconds, plan.Enrichment));
    }

    [Fact]
    public void LargerMachinesGetLargerModelsAndMoreLocalWork()
    {
        MachinePlan balanced = MachineTuning.Plan(32 * GiB, Installed);
        Assert.Equal((MachinePlan.Balanced, "huihui-qwen3.5:9b-q4_K_M", LocalDraftModes.Auto), (balanced.Profile, balanced.LocalModel, balanced.DraftMode));
        MachinePlan performance = MachineTuning.Plan(64 * GiB, Installed);
        Assert.Equal((LocalDraftModes.Always, true), (performance.DraftMode, performance.Enrichment));
        Assert.Contains(MachineTuning.Plan(16 * GiB, [new InstalledModel("big:latest", "x", 9_000_000_000)]).Notes, note => note.Contains("smallest", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoDraftsFollowTheMeasuredPaceAndTheOwnersChoiceWins()
    {
        MemorySettings settings = new();
        LocalPace pace = new();
        LocalDraftPolicy policy = new(settings, pace);

        // Before any measurement one span of any draftable size probes; others wait for its result.
        Assert.True(policy.ShouldDraft(5_000));
        Assert.False(policy.ShouldDraft(2_000));
        pace.Observe(2_000, TimeSpan.FromSeconds(30));
        Assert.False(policy.ShouldDraft(2_000));
        pace.Observe(2_000, TimeSpan.FromSeconds(2));
        pace.Observe(2_000, TimeSpan.FromSeconds(2));
        pace.Observe(2_000, TimeSpan.FromSeconds(2));
        pace.Observe(2_000, TimeSpan.FromSeconds(2));
        Assert.True(policy.ShouldDraft(4_000));

        settings.Write(LocalDraftPolicy.ModeKey, LocalDraftModes.Off);
        Assert.False(policy.ShouldDraft(500));
        settings.Write(LocalDraftPolicy.ModeKey, LocalDraftModes.Always);
        Assert.True(policy.ShouldDraft(6_000));
        Assert.False(policy.ShouldDraft(6_001));
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public string? Read(string key) => values.GetValueOrDefault(key);

        public void Write(string key, string value) => values[key] = value;
    }
}
