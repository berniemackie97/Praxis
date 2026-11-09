using System.Text.Json;

namespace Praxis.Storage;

/// <summary>
/// Turns filesystem rename notifications inside the approved folder into source relocations
/// that keep identity, collections and history, but only after the destination's bytes are
/// re-hashed and match the source's current revision. The event queue is bounded; overflow
/// falls back to ordinary capture of the scanned paths.
/// </summary>
internal sealed class ObservedRenameCorrelator(
    LocalCatalog catalog, LocalSourceIntake intake, string root,
    Action<string> report, Action signal)
{
    private const int MaximumPending = 128;
    private static readonly TimeSpan MaximumEventAge = TimeSpan.FromMinutes(1);
    private readonly Queue<PendingRename> pending = new();
    private readonly Lock gate = new();
    private bool dropped;

    private sealed record PendingRename(string Previous, string Current, DateTimeOffset OccurredUtc);

    internal void Queue(string previousPath, string currentPath)
    {
        if (!InsideRoot(previousPath) || !InsideRoot(currentPath) || previousPath == currentPath)
        {
            return;
        }

        lock (gate)
        {
            if (pending.Count == MaximumPending)
            {
                dropped = true;
            }
            else
            {
                pending.Enqueue(new PendingRename(previousPath, currentPath, DateTimeOffset.UtcNow));
            }
        }

        signal();
    }

    internal void Clear()
    {
        lock (gate)
        {
            pending.Clear();
            dropped = false;
        }
    }

    /// <summary>Applies queued renames within the hash budget; returns the bytes hashed.</summary>
    internal async Task<long> ReconcileAsync(
        IReadOnlyList<LocalObservedFile> files, long hashBudget, CancellationToken cancellationToken)
    {
        bool overflow;
        lock (gate)
        {
            overflow = dropped;
            if (overflow)
            {
                pending.Clear();
                dropped = false;
            }
        }

        if (overflow)
        {
            report("Rename notification backlog exceeded its bound; scanned paths will be captured independently.");
            return 0;
        }

        var byPath = files.ToDictionary(file => file.SourcePath, StringComparer.Ordinal);
        var remaining = hashBudget;
        while (TryPeek() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.OccurredUtc < DateTimeOffset.UtcNow - MaximumEventAge)
            {
                Dequeue();
                continue;
            }

            if (!byPath.TryGetValue(entry.Current, out var file))
            {
                // The destination no longer exists: a later event superseded this one.
                Dequeue();
                continue;
            }

            if (file.Length > remaining)
            {
                break; // Waits for the next scan's budget.
            }

            Dequeue();
            remaining -= file.Length;
            if (File.Exists(entry.Previous) || Directory.Exists(entry.Current) ||
                IntakeFileRules.IsTemporaryName(entry.Current) ||
                file.Length > IntakeFileRules.MaximumFileBytes)
            {
                continue;
            }

            await RelocateAsync(entry, file, cancellationToken);
        }

        return hashBudget - remaining;
    }

    private async Task RelocateAsync(
        PendingRename entry, LocalObservedFile file, CancellationToken cancellationToken)
    {
        try
        {
            intake.ValidateSourcePath(entry.Current, expectDirectory: false);
            if (!IntakeFileRules.IsStable(file))
            {
                return;
            }

            var digest = await IntakeFileRules.HashAsync(entry.Current, cancellationToken);
            if (IntakeFileRules.IsStable(file) &&
                catalog.Sources.RelocateObservedFile(entry.Previous, entry.Current, digest))
            {
                report("Preserved file source identity after observed rename: " +
                    JsonSerializer.Serialize(entry.Previous) + " -> " +
                    JsonSerializer.Serialize(entry.Current));
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            report("Observed file move could not preserve identity: " +
                JsonSerializer.Serialize(entry.Current) + ": " +
                JsonSerializer.Serialize(exception.Message));
        }
    }

    private PendingRename? TryPeek()
    {
        lock (gate)
        {
            return pending.Count == 0 ? null : pending.Peek();
        }
    }

    private void Dequeue()
    {
        lock (gate)
        {
            pending.Dequeue();
        }
    }

    private bool InsideRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return relative != "." && !Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
