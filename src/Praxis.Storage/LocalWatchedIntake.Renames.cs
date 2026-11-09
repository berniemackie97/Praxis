using System.Text.Json;

namespace Praxis.Storage;

public sealed partial class LocalWatchedIntake
{
    private const int MaximumPendingRenames = 128;
    private readonly Queue<PendingRename> renameEvents = new();
    private readonly object renameGate = new();
    private bool droppedRenameEvents;

    private sealed record PendingRename(
        string Previous, string Current, DateTimeOffset OccurredUtc);

    internal void QueueRename(string previousPath, string currentPath)
    {
        if (!InsideApprovedRoot(previousPath) || !InsideApprovedRoot(currentPath) ||
            previousPath == currentPath)
        {
            return;
        }

        lock (renameGate)
        {
            if (renameEvents.Count == MaximumPendingRenames)
            {
                droppedRenameEvents = true;
            }
            else
            {
                renameEvents.Enqueue(new PendingRename(
                    previousPath, currentPath, DateTimeOffset.UtcNow));
            }
        }

        Signal();
    }

    private void ClearPendingRenames()
    {
        lock (renameGate)
        {
            renameEvents.Clear();
            droppedRenameEvents = false;
        }
    }

    private bool InsideApprovedRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return relative != "." && !Path.IsPathRooted(relative) &&
            relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal);
    }

    private async Task ReconcileRenamesAsync(
        IReadOnlyList<LocalObservedFile> files, CancellationToken cancellationToken)
    {
        bool overflow;
        lock (renameGate)
        {
            overflow = droppedRenameEvents;
            if (overflow)
            {
                renameEvents.Clear();
                droppedRenameEvents = false;
            }
        }

        if (overflow)
        {
            report("Rename notification backlog exceeded its bound; scanned paths will be captured independently.");
            return;
        }

        var byPath = files.ToDictionary(f => f.SourcePath, StringComparer.Ordinal);
        var remaining = HashBudgetPerScan;
        while (true)
        {
            PendingRename entry;
            lock (renameGate)
            {
                if (renameEvents.Count == 0)
                {
                    break;
                }

                entry = renameEvents.Peek();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (entry.OccurredUtc < DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1))
            {
                DequeueRename();
                continue;
            }

            if (!byPath.TryGetValue(entry.Current, out var file) || file.Length > remaining)
            {
                // A missing target is a superseded event; a large target waits for the next scan.
                if (!byPath.ContainsKey(entry.Current))
                {
                    DequeueRename();
                    continue;
                }

                break;
            }

            DequeueRename();
            remaining -= file.Length;
            if (File.Exists(entry.Previous) || Directory.Exists(entry.Current) ||
                IsTemporaryName(entry.Current) || file.Length > MaximumFileBytes)
            {
                continue;
            }

            try
            {
                intake.ValidateSourcePath(entry.Current, expectDirectory: false);
                if (!IsStable(file))
                {
                    continue;
                }

                var digest = await HashSourceAsync(entry.Current, cancellationToken);
                if (!IsStable(file))
                {
                    continue;
                }

                if (catalog.RelocateObservedFile(entry.Previous, entry.Current, digest))
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
    }

    private void DequeueRename()
    {
        lock (renameGate)
        {
            renameEvents.Dequeue();
        }
    }
}
