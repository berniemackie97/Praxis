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
    private static readonly TimeSpan MinimumScanSpacing = TimeSpan.FromMilliseconds(250);

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
    private bool rootWasOffline;

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
        bool resuming = status.IsPaused && !pause;
        status.SetPaused(pause);
        if (resuming)
        {
            reconciler.InvalidateAll();
        }

        Signal();
        return status.Snapshot();
    }

    public LocalIntakeStatus RequestRescan()
    {
        Signal();
        return status.Snapshot();
    }

    /// <summary>Runs one reconciliation pass; returns the number of new revisions captured.</summary>
    public async Task<int> ReconcileAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
        (await ReconcileCoreAsync(now, cancellationToken)).Imported;

    private async Task<WatchedScanResult> ReconcileCoreAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await scanLock.WaitAsync(cancellationToken);
        try
        {
            if (status.IsPaused)
            {
                return new WatchedScanResult(0, 0, null);
            }

            WatchedScanResult result = await reconciler.ReconcileAsync(now, cancellationToken);
            if (result.Imported != 0)
            {
                notifyCaptured();
            }

            status.RecordSuccessfulScan(now, result.ObservedFiles, result.Imported);
            return result;
        }
        finally
        {
            scanLock.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        FileSystemWatcher? watcher = null;
        DateTimeOffset lastScanStarted = DateTimeOffset.MinValue;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan delay = rescanInterval;
                if (status.IsPaused)
                {
                    watcher?.Dispose();
                    watcher = null;
                    renames.Clear();
                    status.SetState("paused", "New file capture is paused by the owner.");
                }
                else
                {
                    TimeSpan sincePrevious = DateTimeOffset.UtcNow - lastScanStarted;
                    if (sincePrevious < MinimumScanSpacing)
                    {
                        await Task.Delay(MinimumScanSpacing - sincePrevious, cancellationToken);
                    }

                    lastScanStarted = DateTimeOffset.UtcNow;
                    watcher = MaintainWatcher(watcher);
                    WatchedScanResult? scan = await TryScanAsync(cancellationToken);
                    if (scan is { NextScanDelay: { } due })
                    {
                        delay = due < rescanInterval ? due : rescanInterval;
                    }

                    if (scan is not null && watcher is null && Directory.Exists(root))
                    {
                        status.SetState("degraded", "Filesystem notifications are unavailable; using periodic scans.");
                    }
                }

                await wakeSignal.WaitAsync(delay, cancellationToken);
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

    internal void QueueRename(string previousPath, string currentPath)
    {
        reconciler.Invalidate(previousPath);
        reconciler.Invalidate(currentPath);
        renames.Queue(previousPath, currentPath);
    }

    internal void QueueChanged(string path)
    {
        reconciler.Invalidate(path);
        Signal();
    }

    internal void NotifyWatcherError()
    {
        Interlocked.Exchange(ref watcherFaulted, 1);
        reconciler.InvalidateAll();
        Signal();
    }

    /// <summary>
    /// Keeps a notification watcher matched to the folder's availability: dropped while the
    /// folder is offline, recreated after a watcher error. Rescans continue either way.
    /// </summary>
    private FileSystemWatcher? MaintainWatcher(FileSystemWatcher? watcher)
    {
        if (!Directory.Exists(root))
        {
            status.SetState("offline", OfflineProblem);
            if (!rootWasOffline)
            {
                rootWasOffline = true;
                renames.Clear();
                reconciler.InvalidateAll();
                report("Watched directory is offline; captured originals are retained.");
            }

            watcher?.Dispose();
            return null;
        }

        if (rootWasOffline)
        {
            rootWasOffline = false;
            reconciler.InvalidateAll();
        }

        if (watcher is not null && Interlocked.Exchange(ref watcherFaulted, 0) == 0)
        {
            return watcher;
        }

        if (watcher is not null)
        {
            watcher.Dispose();
            renames.Clear();
            reconciler.InvalidateAll();
        }

        try
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The watched directory cannot be linked.");
            }

            Interlocked.Exchange(ref watcherFaulted, 0);
            FileSystemWatcher created = CreateWatcher();
            status.SetState("resuming");
            report("Watching the approved directory.");
            return created;
        }
        catch (IOException)
        {
            status.SetState("degraded", "Filesystem notifications are unavailable; polling and retrying.");
            report("Watcher setup will retry.");
        }
        catch (UnauthorizedAccessException)
        {
            status.SetState("degraded", "Watcher access denied.");
            report("Watcher setup denied.");
        }
        catch (InvalidDataException)
        {
            status.SetState("degraded", "Watcher configuration rejected.");
            report("Watcher setup rejected.");
        }

        return null;
    }

    private async Task<WatchedScanResult?> TryScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReconcileCoreAsync(DateTimeOffset.UtcNow, cancellationToken);
        }
        catch (IOException)
        {
            bool offline = !Directory.Exists(root);
            status.SetState(offline ? "offline" : "degraded", offline ? OfflineProblem : "Intake scan will retry.");
            report("Intake scan will retry.");
        }
        catch (UnauthorizedAccessException)
        {
            status.SetState("degraded", "Intake scan access denied.");
            report("Intake scan denied.");
        }
        catch (InvalidDataException)
        {
            status.SetState("degraded", "Intake scan rejected input.");
            report("Intake scan rejected input.");
        }

        return null;
    }

    private FileSystemWatcher CreateWatcher()
    {
        FileSystemWatcher watcher = new(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        watcher.Created += (_, change) => QueueChanged(change.FullPath);
        watcher.Changed += (_, change) => QueueChanged(change.FullPath);
        watcher.Deleted += (_, change) => QueueChanged(change.FullPath);
        watcher.Renamed += (_, change) => QueueRename(change.OldFullPath, change.FullPath);
        watcher.Error += (_, _) => NotifyWatcherError();
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
