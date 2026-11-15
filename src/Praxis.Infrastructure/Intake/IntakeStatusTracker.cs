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
    private readonly Action<string>? onStateTransition;
    private string? reportedState;
    private int pauseRequested;

    internal IntakeStatusTracker(Action<string>? onStateTransition = null) =>
        this.onStateTransition = onStateTransition;

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
        string? transition;
        lock (gate)
        {
            current = current with
            {
                State = IsPaused && state != "stopped" ? "paused" : state,
                Paused = IsPaused,
                Problem = problem
            };
            transition = SignificantTransition(current.State);
        }

        if (transition is not null)
        {
            onStateTransition?.Invoke(transition);
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

    internal void RecordSuccessfulScan(DateTimeOffset now, int observedFiles, int imported, bool notificationsUnavailable = false, int skippedLinks = 0)
    {
        string? transition;
        lock (gate)
        {
            IntakeFileError[] reported = fileErrors
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Take(ReportedFileErrors)
                .Select(entry => new IntakeFileError(entry.Key, entry.Value))
                .ToArray();
            bool degraded = fileErrors.Count != 0 || notificationsUnavailable;
            current = new IntakeStatus(IsPaused ? "paused" : degraded ? "degraded" : "watching", IsPaused,
                now, observedFiles, checked(current.ImportedSinceStart + imported), fileErrors.Count,
                Array.AsReadOnly(reported), fileErrors.Count != 0 ? $"{fileErrors.Count} source error(s) need attention." :
                    notificationsUnavailable ? "Filesystem notifications are unavailable; using periodic scans." :
                    skippedLinks > 0 ? $"Skipped {skippedLinks} linked entries; other files will scan normally." : null);
            transition = SignificantTransition(current.State);
        }

        if (transition is not null)
        {
            onStateTransition?.Invoke(transition);
        }
    }

    private string? SignificantTransition(string state)
    {
        if (state is not ("watching" or "offline" or "degraded" or "reapproval_required") ||
            state == reportedState)
        {
            return null;
        }

        reportedState = state;
        return state;
    }
}
