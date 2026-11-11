using Praxis.Domain.Intake;

namespace Praxis.Infrastructure.Intake;

/// <summary>
/// Owner-visible watched-intake state: lifecycle state, pause request and the latest error
/// per source path. Safe to read from any thread while a scan updates it.
/// </summary>
internal sealed class IntakeStatusTracker
{
    private const int ReportedFileErrors = 8;
    private readonly Lock gate = new();
    private readonly Dictionary<string, string> fileErrors = new(StringComparer.Ordinal);
    private IntakeStatus current = new("starting", false, null, 0, 0, 0, [], null);
    private int pauseRequested;

    internal bool IsPaused => Volatile.Read(ref pauseRequested) != 0;

    internal IntakeStatus Snapshot()
    {
        lock (gate)
        {
            return current;
        }
    }

    internal void SetPaused(bool pause)
    {
        Interlocked.Exchange(ref pauseRequested, pause ? 1 : 0);
        SetState(pause ? "paused" : "resuming", pause ? "New file discovery is paused. Already queued processing may finish." : null);
    }

    internal void SetState(string state, string? problem = null)
    {
        lock (gate)
        {
            current = current with
            {
                State = IsPaused && state != "stopped" ? "paused" : state,
                Paused = IsPaused,
                Problem = problem
            };
        }
    }

    /// <summary>Records an error; returns false when the same message is already recorded.</summary>
    internal bool RecordFileError(string path, string message)
    {
        lock (gate)
        {
            if (fileErrors.TryGetValue(path, out string? previous) && previous == message)
            {
                return false;
            }

            fileErrors[path] = message;
            return true;
        }
    }

    internal void ClearFileError(string path)
    {
        lock (gate)
        {
            fileErrors.Remove(path);
        }
    }

    internal void RecordSuccessfulScan(DateTimeOffset now, int observedFiles, int imported)
    {
        lock (gate)
        {
            IntakeFileError[] reported = fileErrors
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(ReportedFileErrors)
                .Select(entry => new IntakeFileError(entry.Key, entry.Value))
                .ToArray();
            current = new IntakeStatus(IsPaused ? "paused" : fileErrors.Count == 0 ? "watching" : "degraded", IsPaused,
                now, observedFiles, checked(current.ImportedSinceStart + imported), fileErrors.Count,
                Array.AsReadOnly(reported), fileErrors.Count == 0 ? null : $"{fileErrors.Count} source error(s) need attention.");
        }
    }
}
