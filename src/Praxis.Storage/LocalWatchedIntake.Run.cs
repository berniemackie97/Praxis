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
                if (!Directory.Exists(root))
                {
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

                        watcher = CreateWatcher();
                        report("Watching the approved directory: " + JsonSerializer.Serialize(root));
                    }
                    catch (IOException exception)
                    {
                        report("Watcher setup will retry: " + JsonSerializer.Serialize(exception.Message));
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        report("Watcher setup denied: " + JsonSerializer.Serialize(exception.Message));
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
                    report("Intake scan will retry: " + JsonSerializer.Serialize(exception.Message));
                }
                catch (UnauthorizedAccessException exception)
                {
                    report("Intake scan denied: " + JsonSerializer.Serialize(exception.Message));
                }
                catch (InvalidDataException exception)
                {
                    report("Intake scan rejected input: " + JsonSerializer.Serialize(exception.Message));
                }

                if (!completedScan)
                {
                    await ProcessPendingAsync(cancellationToken);
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
