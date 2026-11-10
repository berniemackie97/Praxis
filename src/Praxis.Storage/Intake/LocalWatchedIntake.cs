using System.Text.Json;
using Praxis.Storage.Catalog;

namespace Praxis.Storage.Intake;

/// <summary>
/// Owner-approved watched folder. Runs filesystem notifications plus periodic rescans,
/// survives the folder going offline, honors owner pause and signals the processing pipeline
/// after each capture. Scanning, rename correlation and status are separate components.
/// </summary>
public sealed class LocalWatchedIntake
{
    private const string OfflineProblem = "The approved Inbox is unavailable. Reconnect it; captured originals remain preserved.";

    private readonly string root;
    private readonly TimeSpan rescanInterval;
    private readonly Action<string> report;
    private readonly Action notifyCaptured;
    private readonly IntakeStatusTracker status = new();
    private readonly ObservedRenameCorrelator renames;
    private readonly WatchedFileReconciler reconciler;
    private readonly SemaphoreSlim scanLock = new(1, 1);
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private int wakePending;
    private int watcherFaulted;

    public LocalWatchedIntake(LocalCatalog catalog, string intakeDirectory, TimeSpan? quietPeriod = null, TimeSpan? rescanInterval = null, Action<string>? status = null, Action? onCaptured = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(intakeDirectory);
        if (!Path.IsPathFullyQualified(intakeDirectory))
        {
            throw new ArgumentException("The intake directory must be absolute.", nameof(intakeDirectory));
        }

        TimeSpan quiet = quietPeriod ?? TimeSpan.FromSeconds(3);
        if (quiet < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        }

        this.rescanInterval = rescanInterval ?? TimeSpan.FromSeconds(10);
        if (this.rescanInterval < TimeSpan.FromMilliseconds(100))
        {
            throw new ArgumentOutOfRangeException(nameof(rescanInterval));
        }

        // Watcher events, scans and stored locators must agree on one spelling of the folder.
        root = CatalogPaths.Canonicalize(intakeDirectory);
        report = status ?? (_ => { });
        notifyCaptured = onCaptured ?? (() => { });
        LocalSourceIntake intake = new(catalog);
        renames = new ObservedRenameCorrelator(catalog, intake, root, report, Signal);
        reconciler = new WatchedFileReconciler(catalog, intake, root, quiet, renames, this.status, report);
    }

    public LocalIntakeStatus Snapshot() => status.Snapshot();

    public LocalIntakeStatus SetPaused(bool pause)
    {
        status.SetPaused(pause);
        Signal();
        return status.Snapshot();
    }

    public LocalIntakeStatus RequestRescan()
    {
        Signal();
        return status.Snapshot();
    }

    /// <summary>Runs one reconciliation pass; returns the number of new revisions captured.</summary>
    public async Task<int> ReconcileAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await scanLock.WaitAsync(cancellationToken);
        try
        {
            WatchedScanResult result = await reconciler.ReconcileAsync(now, cancellationToken);
            if (result.Imported != 0)
            {
                notifyCaptured();
            }

            status.RecordSuccessfulScan(now, result.ObservedFiles, result.Imported);
            return result.Imported;
        }
        finally
        {
            scanLock.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (status.IsPaused)
                {
                    status.SetState("paused", "New file capture is paused by the owner.");
                }
                else
                {
                    watcher = MaintainWatcher(watcher);
                    if (await TryScanAsync(cancellationToken) && watcher is null && Directory.Exists(root))
                    {
                        status.SetState("degraded", "Filesystem notifications are unavailable; using periodic scans.");
                    }
                }

                await wakeSignal.WaitAsync(rescanInterval, cancellationToken);
                Interlocked.Exchange(ref wakePending, 0);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            watcher?.Dispose();
            status.SetState("stopped");
        }
    }

    internal void QueueRename(string previousPath, string currentPath) => renames.Queue(previousPath, currentPath);

    /// <summary>
    /// Keeps a notification watcher matched to the folder's availability: dropped while the
    /// folder is offline, recreated after a watcher error. Rescans continue either way.
    /// </summary>
    private FileSystemWatcher? MaintainWatcher(FileSystemWatcher? watcher)
    {
        if (!Directory.Exists(root))
        {
            status.SetState("offline", OfflineProblem);
            if (watcher is null)
            {
                return null;
            }

            watcher.Dispose();
            renames.Clear();
            report("Watched directory is offline; captured originals are retained.");

            return null;
        }

        if (watcher is not null && Interlocked.Exchange(ref watcherFaulted, 0) == 0)
        {
            return watcher;
        }

        watcher?.Dispose();
        renames.Clear();
        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The watched directory cannot be linked.");
            }

            Interlocked.Exchange(ref watcherFaulted, 0);
            FileSystemWatcher created = CreateWatcher();
            status.SetState("resuming");
            report("Watching the approved directory: " + JsonSerializer.Serialize(root));
            return created;
        }
        catch (IOException exception)
        {
            status.SetState("degraded", "Filesystem notifications are unavailable; polling and retrying: " + exception.Message);
            report("Watcher setup will retry: " + JsonSerializer.Serialize(exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            status.SetState("degraded", "Watcher access denied: " + exception.Message);
            report("Watcher setup denied: " + JsonSerializer.Serialize(exception.Message));
        }
        catch (InvalidDataException exception)
        {
            status.SetState("degraded", "Watcher configuration rejected: " + exception.Message);
            report("Watcher setup rejected: " + JsonSerializer.Serialize(exception.Message));
        }

        return null;
    }

    private async Task<bool> TryScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileAsync(DateTimeOffset.UtcNow, cancellationToken);
            return true;
        }
        catch (IOException exception)
        {
            bool offline = !Directory.Exists(root);
            status.SetState(offline ? "offline" : "degraded", offline ? OfflineProblem : "Intake scan will retry: " + exception.Message);
            report("Intake scan will retry: " + JsonSerializer.Serialize(exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            status.SetState("degraded", "Intake scan access denied: " + exception.Message);
            report("Intake scan denied: " + JsonSerializer.Serialize(exception.Message));
        }
        catch (InvalidDataException exception)
        {
            status.SetState("degraded", "Intake scan rejected input: " + exception.Message);
            report("Intake scan rejected input: " + JsonSerializer.Serialize(exception.Message));
        }

        return false;
    }

    private FileSystemWatcher CreateWatcher()
    {
        FileSystemWatcher watcher = new(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        watcher.Created += (_, _) => Signal();
        watcher.Changed += (_, _) => Signal();
        watcher.Deleted += (_, _) => Signal();
        watcher.Renamed += (_, change) => renames.Queue(change.OldFullPath, change.FullPath);
        watcher.Error += (_, _) =>
        {
            Interlocked.Exchange(ref watcherFaulted, 1);
            Signal();
        };
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private void Signal()
    {
        if (Interlocked.CompareExchange(ref wakePending, 1, 0) == 0)
        {
            wakeSignal.Release();
        }
    }
}
