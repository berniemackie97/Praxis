namespace Praxis.Application.Inference;

/// <summary>An embedding model bound to its runtime for the duration of one operation.</summary>
public sealed class EmbeddingModelConnection(IEmbeddingModel model, IDisposable? runtime = null) : IDisposable
{
    public IEmbeddingModel Model { get; } = model;

    public void Dispose() => runtime?.Dispose();
}
