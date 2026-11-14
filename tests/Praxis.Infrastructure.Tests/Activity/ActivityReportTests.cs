using System.Text.Json;
using Praxis.Application.Desktop.Actions;
using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Inference;
using Praxis.Application.Policy;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Domain.Verification;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Activity;

/// <summary>The owner can see what each model is doing, how long calls take, and why work waits.</summary>
public sealed class ActivityReportTests : IDisposable
{
    private readonly string folder = Path.Combine(TestPaths.Temp, "praxis-activity-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 11, 14, 15, 0, 0, TimeSpan.Zero));

    public ActivityReportTests()
    {
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task InFlightCallsDurationsReachedLimitsAndFailuresAreReported()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(folder, clock);
        catalog.Verification.SaveProvider(new VerificationProvider(VerificationProvider.Ollama, null, "gemma3", 0, VerificationProvider.Unlimited, true,
            "http://127.0.0.1:11434"));
        catalog.Verification.SaveProvider(new VerificationProvider(VerificationProvider.ClaudeCode, "/usr/bin/true", "sonnet", 10, 2, true));
        catalog.Policy.SaveTier(ModelTiers.Local, VerificationProvider.Ollama);
        catalog.Policy.SaveTier(ModelTiers.Strong, VerificationProvider.ClaudeCode);
        ModelActivity activity = new();
        IReadOnlyList<ActiveModelCall>? during = null;
        DelegatingVerifierModelFactory factory = new(_ => new ScriptedChatModel(_ =>
        {
            during = activity.Snapshot();
            clock.UtcNow = clock.UtcNow.AddSeconds(3);
            return "{}";
        }));
        TieredModels tiers = new(catalog.Policy, catalog.Verification, factory, new DisclosureGate(catalog.Policy, clock), clock, activity);
        ChatRequest request = new([new ChatMessage(ChatRole.User, "x")], JsonDocument.Parse("""{"type":"object"}""").RootElement, 64, 2_048);

        await tiers.CompleteAsync(ModelTiers.Local, new TieredRequest(null, null, null, null, DisclosurePurposes.Intake, request, DataCategories.Documents),
            CancellationToken.None);

        ActiveModelCall call = Assert.Single(during!);
        Assert.Equal((ModelTiers.Local, VerificationProvider.Ollama), (call.Tier, call.ProviderId));
        Assert.Empty(activity.Snapshot());
        using IDisposable running = activity.Begin(new ActiveModelCall(ModelTiers.Strong, VerificationProvider.ClaudeCode, "sonnet",
            DisclosurePurposes.Intake, null, clock.UtcNow));
        foreach (string outcome in new[] { "completed", "model_invalid_response" })
        {
            catalog.Policy.RecordInvocation(new ModelInvocation(ModelTiers.Strong, VerificationProvider.ClaudeCode, true, DisclosurePurposes.Intake,
                DataCategories.Documents, "allowed", null, null, null, "sonnet", 0, 0, outcome, clock.UtcNow));
        }

        ActivityResponse report = new ActivityActions(catalog.Activity, activity, catalog.Verification, catalog.Policy, new LocalPace(), clock).Report(10);

        Assert.Equal(VerificationProvider.ClaudeCode, Assert.Single(report.Calling).ProviderId);
        RecentModelCallAssert(report);
        ProviderActivity claude = Assert.Single(report.Providers, provider => provider.ProviderId == VerificationProvider.ClaudeCode);
        Assert.Equal((2, 2, 1, "model_invalid_response"), (claude.DailyLimit, claude.CallsToday, claude.FailuresLastHour, claude.LastOutcome));
        Assert.Equal([ModelTiers.Strong], claude.Roles);
        Assert.Contains(report.Notices, notice => notice.Text.Contains("used all 2 of today's calls", StringComparison.Ordinal));
        Assert.Contains(report.Notices, notice => notice is { Tone: "critical" } && notice.Text.Contains("could not be used", StringComparison.Ordinal));
    }

    private static void RecentModelCallAssert(ActivityResponse report)
    {
        Assert.Equal(3, report.RecentCalls.Count);
        Assert.Equal(3_000, report.RecentCalls.Single(recent => recent.Tier == ModelTiers.Local).DurationMs);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
