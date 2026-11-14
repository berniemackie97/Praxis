using Praxis.Domain.Verification;
using Xunit;

namespace Praxis.Domain.Tests.Verification;

public sealed class VerificationRouterTests
{
    private static readonly VerificationProvider Local = new(
        VerificationProvider.Ollama, null, "qwen:9b", 0, 3, true, "http://127.0.0.1:11434");
    private static readonly VerificationProvider Codex = new(
        VerificationProvider.Codex, "/bin/codex", null, 10, 2, true);
    private static readonly VerificationProvider Claude = new(
        VerificationProvider.ClaudeCode, "/bin/claude", null, 20, 5, true);

    [Fact]
    public void ImplicitSelectionCanOnlyChooseEnabledLocalModel()
    {
        Assert.Equal(Local, VerificationRouter.Route([new(Claude, 0), new(Codex, 0), new(Local, 1)], null));
        Assert.Null(VerificationRouter.Route([new(Claude, 0), new(Codex, 0)], null));
        Assert.Null(VerificationRouter.Route([new(Local, 3), new(Codex, 0)], null));
        Assert.Null(VerificationRouter.Route([new(Local with { Enabled = false }, 0), new(Codex, 0)], null));
    }

    [Fact]
    public void ExplicitSelectionDoesNotFallbackWhenProviderHasNoAllowance()
    {
        Assert.Equal(Claude, VerificationRouter.Route([new(Local, 0), new(Codex, 0), new(Claude, 4)],
            VerificationProvider.ClaudeCode));
        Assert.Null(VerificationRouter.Route([new(Local, 0), new(Codex, 0), new(Claude, 5)],
            VerificationProvider.ClaudeCode));
        Assert.Null(VerificationRouter.Route([new(Local, 0)], VerificationProvider.Codex));
    }

    [Theory]
    [InlineData("other", 10, 5)]
    [InlineData(VerificationProvider.Codex, -1, 5)]
    [InlineData(VerificationProvider.Codex, 10, -1)]
    [InlineData(VerificationProvider.Codex, 10, 100_001)]
    public void RejectsUnsupportedProvidersAndOutOfRangeLimits(string provider, int weight, int dailyLimit) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            new VerificationProvider(provider, "/bin/x", null, weight, dailyLimit, true).EnsureValid());

    [Fact]
    public void LocalRequiresAnActualLoopbackEndpointAndCannotTakeCliExecutable()
    {
        Local.EnsureValid();
        Assert.ThrowsAny<ArgumentException>(() => (Local with { Endpoint = "https://api.example.com" }).EnsureValid());
        Assert.ThrowsAny<ArgumentException>(() => (Local with { Executable = "/bin/ollama" }).EnsureValid());
        Assert.ThrowsAny<ArgumentException>(() => (Local with { Model = null }).EnsureValid());
        Assert.ThrowsAny<ArgumentException>(() => (Codex with { Endpoint = "http://127.0.0.1:11434" }).EnsureValid());
    }
}
