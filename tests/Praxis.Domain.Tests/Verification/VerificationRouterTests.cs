using Praxis.Domain.Verification;
using Xunit;

namespace Praxis.Domain.Tests.Verification;

public sealed class VerificationRouterTests
{
    private static readonly VerificationProvider Codex = new(VerificationProvider.Codex, "/bin/codex", null, 10, 2, true);
    private static readonly VerificationProvider Claude = new(VerificationProvider.ClaudeCode, "/bin/claude", null, 20, 5, true);

    [Fact]
    public void RoutesToTheCheapestProviderWithAllowance()
    {
        Assert.Equal(Codex, VerificationRouter.Route([new(Claude, 0), new(Codex, 1)], null));
        Assert.Equal(Claude, VerificationRouter.Route([new(Claude, 0), new(Codex, 2)], null));
    }

    [Fact]
    public void HonorsARequestedProviderOnlyWhileItHasAllowance()
    {
        Assert.Equal(Claude, VerificationRouter.Route([new(Codex, 0), new(Claude, 4)], VerificationProvider.ClaudeCode));
        Assert.Null(VerificationRouter.Route([new(Codex, 0), new(Claude, 5)], VerificationProvider.ClaudeCode));
    }

    [Fact]
    public void BreaksEqualWeightsByProviderIdentifier()
    {
        VerificationProvider equal = Claude with { CostWeight = Codex.CostWeight };
        Assert.Equal(equal, VerificationRouter.Route([new(Codex, 0), new(equal, 0)], null));
    }

    [Theory]
    [InlineData("other", 10, 5)]
    [InlineData(VerificationProvider.Codex, -1, 5)]
    [InlineData(VerificationProvider.Codex, 10, 0)]
    [InlineData(VerificationProvider.Codex, 10, 501)]
    public void RejectsUnsupportedProvidersAndOutOfRangeLimits(string provider, int weight, int dailyLimit) =>
        Assert.ThrowsAny<ArgumentException>(() => new VerificationProvider(provider, "/bin/x", null, weight, dailyLimit, true).EnsureValid());
}
