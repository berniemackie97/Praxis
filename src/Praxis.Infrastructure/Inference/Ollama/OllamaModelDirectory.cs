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
        InstalledModel[] models = (await runtime.ListModelsAsync(cancellationToken)).ToArray();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, models.Length),
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = deadline.Token },
                async (index, token) =>
                {
                    InstalledModel model = models[index];
                    try
                    {
                        models[index] = model with { Capabilities = await runtime.ReadCapabilitiesAsync(model.Name, token) };
                    }
                    catch (InferenceException)
                    {
                        // A stale model or older server need not hide the rest of the inventory.
                    }
                });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Return available metadata rather than block the settings page.
        }

        return models;
    }
}
