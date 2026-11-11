using Praxis.Infrastructure.Catalog;

namespace Praxis.Infrastructure.Intake;

/// <summary>
/// Turns filesystem rename notifications inside the approved folder into source relocations
/// that keep identity, collections and history, but only after the destination's bytes are
/// re-hashed and match the source's current revision. The event queue is bounded; overflow
/// falls back to ordinary capture of the scanned paths.
/// </summary>
internal sealed class ObservedRenameCorrelator(LocalCatalog catalog, LocalSourceIntake intake, string root, Action<string> report, Action signal)
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
    internal async Task<long> ReconcileAsync(IReadOnlyList<LocalObservedFile> files, long hashBudget, CancellationToken cancellationToken)
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

        Dictionary<string, LocalObservedFile> byPath = files.ToDictionary(file => file.SourcePath, StringComparer.Ordinal);
        long remaining = hashBudget;
        while (TryPeek() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.OccurredUtc < DateTimeOffset.UtcNow - MaximumEventAge)
            {
                Dequeue(entry);
                continue;
            }

            (long hashed, bool complete) = await ReconcileRenameAsync(entry, byPath, remaining, cancellationToken);
            remaining -= hashed;
            if (!complete)
            {
                break;
            }

            Dequeue(entry);
        }

        return hashBudget - remaining;
    }

    private async Task<(long Hashed, bool Complete)> ReconcileRenameAsync(PendingRename entry,
        Dictionary<string, LocalObservedFile> byPath, long remaining, CancellationToken cancellationToken)
    {
        if (byPath.TryGetValue(entry.Current, out LocalObservedFile? file))
        {
            if (file.Length > remaining)
            {
                return (0, false);
            }

            if (File.Exists(entry.Previous) || IntakeFileRules.IsTemporaryName(entry.Current) ||
                file.Length > IntakeFileRules.MaximumFileBytes)
            {
                return (0, true);
            }

            await RelocateAsync(entry.Previous, entry.Current, file, cancellationToken);
            return (file.Length, true);
        }

        if (!Directory.Exists(entry.Current) || Directory.Exists(entry.Previous) || File.Exists(entry.Previous))
        {
            return (0, true);
        }

        return await RelocateDirectoryAsync(entry, byPath, remaining, cancellationToken);
    }

    private async Task<(long Hashed, bool Complete)> RelocateDirectoryAsync(PendingRename entry,
        Dictionary<string, LocalObservedFile> byPath, long remaining, CancellationToken cancellationToken)
    {
        long hashed = 0;
        try
        {
            intake.ValidateSourcePath(entry.Current, expectDirectory: true);
            string previousPrefix = Path.TrimEndingDirectorySeparator(entry.Previous) + Path.DirectorySeparatorChar;
            foreach (var (_, locator, _) in catalog.Sources.ActiveFileLocators())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!locator.StartsWith(previousPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string currentPath = Path.Join(entry.Current, locator[previousPrefix.Length..]);
                if (!InsideRoot(currentPath) || !byPath.TryGetValue(currentPath, out LocalObservedFile? file) ||
                    IntakeFileRules.IsTemporaryName(currentPath) || file.Length > IntakeFileRules.MaximumFileBytes)
                {
                    continue;
                }

                if (file.Length > remaining - hashed)
                {
                    return (hashed, false);
                }

                hashed += file.Length;
                await RelocateAsync(locator, currentPath, file, cancellationToken);
            }

            return (hashed, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            report("Observed directory move could not preserve identities.");
            return (hashed, true);
        }
    }

    private async Task RelocateAsync(string previous, string current, LocalObservedFile file, CancellationToken cancellationToken)
    {
        try
        {
            intake.ValidateSourcePath(current, expectDirectory: false);
            if (!IntakeFileRules.IsStable(file))
            {
                return;
            }

            string digest = await IntakeFileRules.HashAsync(current, cancellationToken);
            if (IntakeFileRules.IsStable(file) && catalog.Sources.RelocateObservedFile(previous, current, digest))
            {
                report("Preserved file source identity after observed rename.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            report("Observed file move could not preserve identity.");
        }
    }

    private PendingRename? TryPeek()
    {
        lock (gate)
        {
            return pending.Count == 0 ? null : pending.Peek();
        }
    }

    private void Dequeue(PendingRename entry)
    {
        lock (gate)
        {
            if (pending.TryPeek(out PendingRename? next) && ReferenceEquals(next, entry))
            {
                pending.Dequeue();
            }
        }
    }

    private bool InsideRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return false;
        }

        string relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
