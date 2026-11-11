using Praxis.Application.Inference;
using Praxis.Domain.Models;

namespace Praxis.Infrastructure.Inference.Ollama;

/// <summary>Lists the models installed in a loopback Ollama runtime.</summary>
public sealed class OllamaModelDirectory : ILocalModelDirectory
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<InstalledModel>> ListInstalledAsync(string endpoint, CancellationToken cancellationToken)
    {
        using OllamaRuntime runtime = new(endpoint, RequestTimeout);
        return await runtime.ListModelsAsync(cancellationToken);
    }
}
