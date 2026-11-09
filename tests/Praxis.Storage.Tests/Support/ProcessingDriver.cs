using Praxis.Storage;

namespace Praxis.Storage.Tests;

/// <summary>
/// Steps one handler's jobs deterministically, as the pipeline would, so a test can assert
/// each stage's effect in isolation without running the background loop.
/// </summary>
internal sealed class ProcessingDriver(LocalCatalog catalog, ILocalProcessingHandler handler)
{
    private bool reopenExhausted;

    internal static ProcessingDriver Text(LocalCatalog catalog) =>
        new(catalog, new LocalTextProcessor(catalog));

    internal static ProcessingDriver Topics(LocalCatalog catalog) =>
        new(catalog, new LocalHeadingProposer(catalog));

    internal async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        var lease = Claim();
        if (lease is null && !reopenExhausted)
        {
            if (handler.TryReopenWork())
            {
                lease = Claim();
            }
            else
            {
                reopenExhausted = true;
            }
        }

        if (lease is null)
        {
            return false;
        }

        await handler.ProcessAsync(lease, cancellationToken);
        return true;
    }

    /// <summary>Synchronous step for handlers that complete without awaiting I/O.</summary>
    internal bool ProcessNext() => ProcessNextAsync().GetAwaiter().GetResult();

    private ProcessingLease? Claim() =>
        catalog.Processing.TryClaim(handler.LeaseDuration, processorKey: handler.ProcessorKey);
}
