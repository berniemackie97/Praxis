namespace Praxis.Storage;

public sealed record LocalIntakeFileError(string SourcePath, string Message);

public sealed record LocalIntakeStatus(
    string State, bool Paused, DateTimeOffset? LastSuccessfulScanUtc,
    int ObservedFiles, int ImportedSinceStart, int ErrorCount,
    IReadOnlyList<LocalIntakeFileError> FileErrors, string? Problem);

public sealed partial class LocalWatchedIntake
{
    private readonly object statusGate = new();
    private LocalIntakeStatus currentStatus = new(
        "starting", false, null, 0, 0, 0, [], null);
    private int pauseRequested;

    public LocalIntakeStatus Snapshot()
    {
        lock (statusGate)
        {
            return currentStatus;
        }
    }

    public LocalIntakeStatus SetPaused(bool pause)
    {
        Interlocked.Exchange(ref pauseRequested, pause ? 1 : 0);
        SetState(pause ? "paused" : "resuming",
            pause ? "New file discovery is paused. Already queued processing may finish." : null);
        Signal();
        return Snapshot();
    }

    public LocalIntakeStatus RequestRescan()
    {
        Signal();
        return Snapshot();
    }

    private bool IsPaused => Volatile.Read(ref pauseRequested) != 0;

    private void SetState(string state, string? problem = null)
    {
        lock (statusGate)
        {
            currentStatus = currentStatus with
            {
                State = IsPaused && state != "stopped" ? "paused" : state,
                Paused = IsPaused,
                Problem = problem
            };
        }
    }

    private void RecordSuccessfulScan(DateTimeOffset now, int observedFiles, int imported)
    {
        var errorsSnapshot = errors
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(8)
            .Select(entry => new LocalIntakeFileError(entry.Key, entry.Value))
            .ToArray();

        lock (statusGate)
        {
            currentStatus = new LocalIntakeStatus(
                IsPaused ? "paused" : errors.Count == 0 ? "watching" : "degraded",
                IsPaused, now, observedFiles,
                checked(currentStatus.ImportedSinceStart + imported),
                errors.Count, errorsSnapshot,
                errors.Count == 0 ? null :
                    $"{errors.Count} source error(s) need attention.");
        }
    }
}
