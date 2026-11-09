using System.Text.Json;

namespace Praxis.Storage;

public sealed partial class LocalWatchedIntake
{
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private int wakePending;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The selected intake directory is unavailable.");
        }

        using var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        FileSystemEventHandler changed = (_, _) => Signal();
        RenamedEventHandler renamed = (_, _) => Signal();
        ErrorEventHandler error = (_, _) =>
        {
            report("Filesystem watch interrupted; the next reconciliation will rescan.");
            Signal();
        };
        watcher.Created += changed;
        watcher.Changed += changed;
        watcher.Deleted += changed;
        watcher.Renamed += renamed;
        watcher.Error += error;
        watcher.EnableRaisingEvents = true;

        report("Watching the approved intake directory: " + JsonSerializer.Serialize(root));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(DateTimeOffset.UtcNow, cancellationToken);
                }
                catch (IOException exception)
                {
                    report("Intake scan failed; will retry: " + JsonSerializer.Serialize(exception.Message));
                }
                catch (UnauthorizedAccessException exception)
                {
                    report("Intake scan was denied; will retry: " + JsonSerializer.Serialize(exception.Message));
                }
                catch (InvalidDataException exception)
                {
                    report("Intake scan rejected unsafe input: " + JsonSerializer.Serialize(exception.Message));
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
            watcher.EnableRaisingEvents = false;
        }
    }

    private void Signal()
    {
        if (Interlocked.CompareExchange(ref wakePending, 1, 0) == 0)
        {
            wakeSignal.Release();
        }
    }
}
