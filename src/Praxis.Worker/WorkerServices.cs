namespace Praxis.Worker;

/// <summary>
/// Runs cooperating long-lived services under one lifetime: the first service to finish or
/// fault cancels the others, and every service is awaited so a fault is surfaced, not lost.
/// </summary>
internal static class WorkerServices
{
    internal static async Task RunAsync(
        CancellationToken cancellationToken, params Func<CancellationToken, Task>[] services)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = services.Select(service => service(lifetime.Token)).ToArray();
        await Task.WhenAny(running);
        await lifetime.CancelAsync();
        await Task.WhenAll(running);
    }
}
