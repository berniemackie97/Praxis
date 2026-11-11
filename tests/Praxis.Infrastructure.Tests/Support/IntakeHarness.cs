using Praxis.Application.Processing;
using Praxis.Domain.Intake;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Tests.Support;

/// <summary>
/// Watched intake and the processing pipeline composed as the worker composes them:
/// captures wake processing, and the two run as independent services.
/// </summary>
internal sealed class IntakeHarness
{
    internal IntakeHarness(
        SqliteCatalog catalog, string inbox, TimeSpan? quietPeriod = null,
        TimeSpan? rescanInterval = null, Action<string>? status = null)
    {
        Processing = CatalogProcessing.CreatePipeline(catalog, TimeSpan.FromMilliseconds(100));
        Monitor = new WatchedFolderIntake(catalog.Sources, catalog.Originals, catalog.DataDirectory, inbox, quietPeriod, rescanInterval, status,
            onCaptured: Processing.Wake);
    }

    internal WatchedFolderIntake Monitor { get; }
    internal ProcessingPipeline Processing { get; }

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

    internal IntakeStatus Snapshot() => Monitor.Snapshot();
    internal IntakeStatus SetPaused(bool pause) => Monitor.SetPaused(pause);
    internal IntakeStatus RequestRescan() => Monitor.RequestRescan();
}
