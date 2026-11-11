namespace Praxis.Worker;

/// <summary>
/// Stops all cooperating services when one ends, then awaits every service before releasing
/// the catalog. Only a normal end of the desktop input service is an expected completion.
/// </summary>
internal static class WorkerServices
{
    internal static async Task RunAsync(CancellationToken cancellationToken, bool allowDesktopExit, params Func<CancellationToken, Task>[] services)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task[] running = services.Select(service => StartAsync(service, lifetime.Token)).ToArray();
        Task first = await Task.WhenAny(running);
        await lifetime.CancelAsync();
        try
        {
            await Task.WhenAll(running);
        }
        catch (OperationCanceledException) when (!first.IsFaulted)
        {
        }

        if (!cancellationToken.IsCancellationRequested &&
            (first.IsCanceled || !allowDesktopExit || !ReferenceEquals(first, running[0])))
        {
            throw new InvalidOperationException("A worker background service stopped unexpectedly.");
        }
    }

    private static async Task StartAsync(Func<CancellationToken, Task> service, CancellationToken cancellationToken) =>
        await service(cancellationToken);
}
