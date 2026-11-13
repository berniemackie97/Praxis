using Praxis.Application.Agents;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed class SessionSecretStoreTests
{
    [Fact]
    public void AppProvidedKeysWinAreRemovableAndFallBackToTheKeychain()
    {
        SessionSecretStore store = new(new FixedStore("from-keychain"));
        Assert.Equal("from-keychain", store.Read("anthropic"));
        store.Set("anthropic", "from-app");
        Assert.Equal("from-app", store.Read("anthropic"));
        Assert.True(store.Remove("anthropic"));
        Assert.False(store.Remove("anthropic"));
        Assert.Equal("from-keychain", store.Read("anthropic"));
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("account", "")]
    [InlineData("account", null)]
    public void InvalidKeysAreRejected(string account, string? secret) =>
        Assert.Throws<ArgumentException>(() => new SessionSecretStore(new FixedStore(null)).Set(account, secret!));

    [Fact]
    public void TheNumberOfLoadedKeysIsBounded()
    {
        SessionSecretStore store = new(new FixedStore(null));
        for (int index = 0; index < 64; index++)
        {
            store.Set($"account-{index}", "key");
        }

        Assert.Throws<InvalidOperationException>(() => store.Set("one-more", "key"));
        store.Set("account-0", "replaced");
        Assert.Equal("replaced", store.Read("account-0"));
    }

    private sealed class FixedStore(string? secret) : ISecretStore
    {
        public string? Read(string account) => secret;
    }
}
