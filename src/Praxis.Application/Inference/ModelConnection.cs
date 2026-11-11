namespace Praxis.Application.Inference;

/// <summary>A chat model bound to its runtime for the duration of one job.</summary>
public sealed class ModelConnection(IChatModel model, IDisposable? runtime = null) : IDisposable
{
    public IChatModel Model { get; } = model;

    public void Dispose() => runtime?.Dispose();
}
