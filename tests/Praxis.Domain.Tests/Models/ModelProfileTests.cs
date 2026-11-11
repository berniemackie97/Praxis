using Praxis.Domain.Models;
using Xunit;

namespace Praxis.Domain.Tests.Models;

public sealed class ModelProfileTests
{
    [Fact]
    public void NormalizesTheEndpointToItsOriginAndTrimsTheModel()
    {
        ModelProfile profile = new ModelProfile(ModelProfile.LocalEnrichment, ModelProfile.OllamaProvider,
            "http://127.0.0.1:11434", " qwen:9b ", 16_384, true).Normalized();

        Assert.Equal(("http://127.0.0.1:11434/", "qwen:9b"), (profile.Endpoint, profile.Model));
    }

    [Theory]
    [InlineData("remote-role", "ollama", "http://127.0.0.1:11434", "qwen", 16_384)]
    [InlineData(ModelProfile.LocalEmbedding, "openai", "http://127.0.0.1:11434", "qwen", 16_384)]
    [InlineData(ModelProfile.LocalEmbedding, "ollama", "http://192.0.2.10:11434", "qwen", 16_384)]
    [InlineData(ModelProfile.LocalEmbedding, "ollama", "http://127.0.0.1:11434", "qwen 9b", 16_384)]
    [InlineData(ModelProfile.LocalEmbedding, "ollama", "http://127.0.0.1:11434", "qwen", 1_024)]
    public void RejectsUnsupportedRolesProvidersRemoteEndpointsAndBadBounds(string role, string provider, string endpoint, string model, int context) =>
        Assert.ThrowsAny<ArgumentException>(() => new ModelProfile(role, provider, endpoint, model, context, true).Normalized());
}
