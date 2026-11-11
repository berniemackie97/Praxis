using Praxis.Application.Inference;
using Praxis.Domain.Models;

namespace Praxis.Infrastructure.Inference.Ollama;

/// <summary>Connects Ollama-backed embedding profiles to a loopback Ollama runtime.</summary>
public sealed class OllamaEmbeddingModelConnector : IEmbeddingModelConnector
{
    public EmbeddingModelConnection Connect(ModelProfile profile, TimeSpan? requestTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Provider != ModelProfile.OllamaProvider)
        {
            throw new NotSupportedException("The model provider is not supported.");
        }

        OllamaRuntime ollama = new(profile.Endpoint, requestTimeout);
        return new EmbeddingModelConnection(ollama.CreateEmbeddingModel(profile.Model), ollama);
    }
}
