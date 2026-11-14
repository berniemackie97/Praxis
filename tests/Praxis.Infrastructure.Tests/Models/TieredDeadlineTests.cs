using System.Text.Json;
using Praxis.Application.Inference;
using Praxis.Application.Policy;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Domain.Verification;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Models;

/// <summary>A local draft stops at its deadline instead of holding a worker for the model's full timeout.</summary>
public sealed class TieredDeadlineTests : IDisposable
{
    private readonly string folder = Path.Combine(TestPaths.Temp, "praxis-deadline-" + Guid.NewGuid().ToString("N"));

    public TieredDeadlineTests()
    {
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task ACallPastItsDeadlineIsRecordedAsATimeout()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(folder);
        catalog.Verification.SaveProvider(new VerificationProvider(VerificationProvider.Ollama, null, "gemma3", 0, VerificationProvider.Unlimited, true,
            "http://127.0.0.1:11434"));
        catalog.Policy.SaveTier(ModelTiers.Local, VerificationProvider.Ollama);
        DelegatingVerifierModelFactory factory = new(_ => new SlowChatModel());
        TieredModels tiers = new(catalog.Policy, catalog.Verification, factory, new DisclosureGate(catalog.Policy, TimeProvider.System), TimeProvider.System);
        ChatRequest request = new([new ChatMessage(ChatRole.User, "x")], JsonDocument.Parse("""{"type":"object"}""").RootElement, 64, 2_048);

        TieredOutcome outcome = await tiers.CompleteAsync(ModelTiers.Local,
            new TieredRequest(null, null, null, null, DisclosurePurposes.Intake, request, DataCategories.Documents, Deadline: TimeSpan.FromMilliseconds(50)),
            CancellationToken.None);

        Assert.Equal((TieredOutcome.Failed, "model_timeout"), (outcome.Status, outcome.FailureCode));
        Assert.Equal("model_timeout", Assert.Single(catalog.Policy.ReadInvocations(10)).Outcome);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class SlowChatModel : IChatModel
    {
        public Task<ModelIdentity> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ModelIdentity("ollama", "gemma3", "d", null, null, null));

        public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
