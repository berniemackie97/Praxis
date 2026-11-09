using Praxis.Inference;

namespace Praxis.Storage;

/// <summary>A chat model bound to its runtime for the duration of one job.</summary>
public sealed class ModelConnection(IChatModel model, IDisposable? runtime = null) : IDisposable
{
    public IChatModel Model { get; } = model;

    /// <summary>Opens the configured local runtime for a profile.</summary>
    public static ModelConnection Open(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Provider != OllamaRuntime.ProviderName)
        {
            throw new NotSupportedException("The model provider is not supported.");
        }

        var ollama = new OllamaRuntime(profile.Endpoint);
        return new ModelConnection(ollama.CreateChatModel(profile.Model), ollama);
    }

    public void Dispose() => runtime?.Dispose();
}
