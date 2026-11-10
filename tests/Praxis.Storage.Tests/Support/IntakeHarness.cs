using Praxis.Storage.Catalog;
using Praxis.Storage.Intake;
using Praxis.Storage.Processing;

namespace Praxis.Storage.Tests.Support;

/// <summary>
/// Watched intake and the processing pipeline composed as the worker composes them:
/// captures wake processing, and the two run as independent services.
/// </summary>
internal sealed class IntakeHarness
{
    internal IntakeHarness(
        LocalCatalog catalog, string inbox, TimeSpan? quietPeriod = null,
        TimeSpan? rescanInterval = null, Action<string>? status = null)
    {
        Processing = LocalProcessingPipeline.CreateDefault(catalog, TimeSpan.FromMilliseconds(100));
        Monitor = new LocalWatchedIntake(catalog, inbox, quietPeriod, rescanInterval, status,
            onCaptured: Processing.Wake);
    }

    internal LocalWatchedIntake Monitor { get; }
    internal LocalProcessingPipeline Processing { get; }

    /// <summary>One intake scan followed by processing everything it made available.</summary>
    internal async Task<int> ReconcileAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        int imported = await Monitor.ReconcileAsync(now, cancellationToken);
        while (await Processing.ProcessNextAsync(cancellationToken))
        {
        }

        return imported;
    }

    internal Task RunAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(Monitor.RunAsync(cancellationToken), Processing.RunAsync(cancellationToken));

    internal LocalIntakeStatus Snapshot() => Monitor.Snapshot();
    internal LocalIntakeStatus SetPaused(bool pause) => Monitor.SetPaused(pause);
    internal LocalIntakeStatus RequestRescan() => Monitor.RequestRescan();
}
