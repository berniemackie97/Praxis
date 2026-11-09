using System.Text.Json;

namespace Praxis.Storage;

public sealed partial class LocalWatchedIntake
{
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private int wakePending;
    private int watcherFaulted;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (IsPaused)
                {
                    SetState("paused", "New file capture is paused by the owner.");
                    await ProcessPendingAsync(cancellationToken);
                    await wakeSignal.WaitAsync(rescanInterval, cancellationToken);
                    Interlocked.Exchange(ref wakePending, 0);
                    continue;
                }

                if (!Directory.Exists(root))
                {
                    SetState("offline", "The approved Inbox is unavailable. Reconnect it; captured originals remain preserved.");
                    if (watcher is not null)
                    {
                        watcher.Dispose();
                        watcher = null;
                        report("Watched directory is offline; captured originals are retained.");
                    }
                }
                else if (watcher is null || Interlocked.Exchange(ref watcherFaulted, 0) != 0)
                {
                    watcher?.Dispose();
                    watcher = null;
                    try
                    {
                        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new InvalidDataException("The watched directory cannot be linked.");
                        }

                        Interlocked.Exchange(ref watcherFaulted, 0);
                        watcher = CreateWatcher();
                        SetState("resuming");
                        report("Watching the approved directory: " + JsonSerializer.Serialize(root));
                    }
                    catch (IOException exception)
                    {
                        SetState("degraded", "Filesystem notifications are unavailable; polling and retrying: " + exception.Message);
                        report("Watcher setup will retry: " + JsonSerializer.Serialize(exception.Message));
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        SetState("degraded", "Watcher access denied: " + exception.Message);
                        report("Watcher setup denied: " + JsonSerializer.Serialize(exception.Message));
                    }
                    catch (InvalidDataException exception)
                    {
                        SetState("degraded", "Watcher configuration rejected: " + exception.Message);
                        report("Watcher setup rejected: " + JsonSerializer.Serialize(exception.Message));
                    }
                }

                var completedScan = false;
                try
                {
                    await ReconcileAsync(DateTimeOffset.UtcNow, cancellationToken);
                    completedScan = true;
                }
                catch (IOException exception)
                {
                    SetState(Directory.Exists(root) ? "degraded" : "offline",
                        "Intake scan will retry: " + exception.Message);
                    report("Intake scan will retry: " + JsonSerializer.Serialize(exception.Message));
                }
                catch (UnauthorizedAccessException exception)
                {
                    SetState("degraded", "Intake scan access denied: " + exception.Message);
                    report("Intake scan denied: " + JsonSerializer.Serialize(exception.Message));
                }
                catch (InvalidDataException exception)
                {
                    SetState("degraded", "Intake scan rejected input: " + exception.Message);
                    report("Intake scan rejected input: " + JsonSerializer.Serialize(exception.Message));
                }

                if (!completedScan)
                {
                    await ProcessPendingAsync(cancellationToken);
                }
                else if (watcher is null && Directory.Exists(root))
                {
                    SetState("degraded", "Filesystem notifications are unavailable; using periodic scans.");
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
            SetState("stopped");
        }
    }

    private FileSystemWatcher CreateWatcher()
    {
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        FileSystemEventHandler changed = (_, _) => Signal();
        RenamedEventHandler renamed = (_, _) => Signal();
        ErrorEventHandler error = (_, _) =>
        {
            Interlocked.Exchange(ref watcherFaulted, 1);
            Signal();
        };
        watcher.Created += changed;
        watcher.Changed += changed;
        watcher.Deleted += changed;
        watcher.Renamed += renamed;
        watcher.Error += error;
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
