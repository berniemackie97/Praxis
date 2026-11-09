using System.Collections.Concurrent;
using System.Text.Json;

namespace Praxis.Storage;

public sealed partial class LocalWatchedIntake
{
    private const int MaximumPendingRenames = 128;
    private readonly ConcurrentQueue<PendingRename> renameEvents = new();
    private int queuedRenames;
    private int droppedRenameEvents;

    private sealed record PendingRename(
        string Previous, string Current, DateTimeOffset OccurredUtc);

    internal void QueueRename(string previousPath, string currentPath)
    {
        if (!InsideApprovedRoot(previousPath) || !InsideApprovedRoot(currentPath) ||
            previousPath == currentPath)
        {
            return;
        }

        if (Interlocked.Increment(ref queuedRenames) > MaximumPendingRenames)
        {
            Interlocked.Decrement(ref queuedRenames);
            Interlocked.Exchange(ref droppedRenameEvents, 1);
            Signal();
            return;
        }

        renameEvents.Enqueue(new PendingRename(previousPath, currentPath, DateTimeOffset.UtcNow));
        Signal();
    }

    private void ClearPendingRenames()
    {
        while (renameEvents.TryDequeue(out _))
        {
            Interlocked.Decrement(ref queuedRenames);
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
        if (Interlocked.Exchange(ref droppedRenameEvents, 0) != 0)
        {
            ClearPendingRenames();
            report("Rename notification backlog exceeded its bound; scanned paths will be captured independently.");
            return;
        }

        var byPath = files.ToDictionary(f => f.SourcePath, StringComparer.Ordinal);
        var remaining = HashBudgetPerScan;
        while (renameEvents.TryPeek(out var entry))
        {
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
            catch (IOException)
            {
                // Normal capture reconciliation will retry the destination without reassigning identity.
            }
            catch (UnauthorizedAccessException)
            {
                // The destination remains subject to the normal permission/error path.
            }
            catch (InvalidDataException)
            {
                // Linked, unstable or rejected targets must not inherit source identity.
            }
            catch (InvalidOperationException)
            {
                // An occupied or revoked source cannot be moved automatically.
            }
        }
    }

    private void DequeueRename()
    {
        if (renameEvents.TryDequeue(out _))
        {
            Interlocked.Decrement(ref queuedRenames);
        }
    }
}
