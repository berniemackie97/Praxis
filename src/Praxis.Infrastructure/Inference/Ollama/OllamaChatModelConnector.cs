using Praxis.Application.Inference;
using Praxis.Domain.Models;

namespace Praxis.Infrastructure.Inference.Ollama;

/// <summary>Connects Ollama-backed model profiles to a loopback Ollama runtime.</summary>
public sealed class OllamaChatModelConnector : IChatModelConnector
{
    public ModelConnection Connect(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Provider != ModelProfile.OllamaProvider)
        {
            throw new NotSupportedException("The model provider is not supported.");
        }

        OllamaRuntime ollama = new(profile.Endpoint);
        return new ModelConnection(ollama.CreateChatModel(profile.Model), ollama);
    }
}
