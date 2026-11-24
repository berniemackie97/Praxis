using System.Security.Cryptography;
using System.Text;
using Praxis.Application.Sources;
using Praxis.Domain.Intake;
using Praxis.Domain.Originals;
using Praxis.Domain.Sources;

namespace Praxis.Infrastructure.Intake;

/// <summary>
/// One reconciliation pass over the approved folder: applies observed renames, waits for each
/// file to stay unchanged for the quiet period, then captures new content as a revision with a
/// deterministic request key. Unchanged verified files are re-hashed only periodically.
/// Not thread-safe; the owning intake serializes passes.
/// </summary>
internal sealed class WatchedFileReconciler(ISourceRepository sources, FileSourceIntake intake, string root, TimeSpan quietPeriod, ObservedRenameCorrelator renames, IntakeStatusTracker status, Action<string> report, int pageSize = 10_000)
{
    private const int MaximumQueuedChanges = 512;
    private const int MaximumDepth = 12;
    private const int MaximumCachedFiles = 100_000;
    private const long MaximumFolderBytes = 256L * 1024 * 1024 * 1024;
    private const long HashBudgetPerScan = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan FullVerificationInterval = TimeSpan.FromMinutes(30);
    private bool locatorsNormalized;
    private bool invalidateEverything;
    private string? scanCursor;
    private readonly Lock pendingChangesGate = new();
    private readonly HashSet<string> pendingChanges = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ObservedState> observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VerifiedState> verified = new(StringComparer.Ordinal);

    private sealed record FileStamp(long Length, DateTime LastWriteUtc);
    private sealed record ObservedState(FileStamp Stamp, DateTimeOffset FirstSeen);
    private sealed record VerifiedState(FileStamp Stamp, DateTimeOffset LastVerified);

    internal void Invalidate(string path)
    {
        lock (pendingChangesGate)
        {
            if (invalidateEverything || !pendingChanges.Add(path))
            {
                return;
            }

            if (pendingChanges.Count > MaximumQueuedChanges)
            {
                pendingChanges.Clear();
                invalidateEverything = true;
            }
        }
    }

    internal void InvalidateAll()
    {
        lock (pendingChangesGate)
        {
            pendingChanges.Clear();
            invalidateEverything = true;
        }
    }

    private void ApplyInvalidations()
    {
        lock (pendingChangesGate)
        {
            if (invalidateEverything)
            {
                observations.Clear();
                verified.Clear();
                scanCursor = null;
            }
            else
            {
                foreach (string path in pendingChanges)
                {
                    observations.Remove(path);
                    verified.Remove(path);
                }
            }

            pendingChanges.Clear();
            invalidateEverything = false;
        }
    }

    /// <summary>
    /// Revokes sources older versions captured from version-control internals, dependency folders
    /// or secrets files, which capture now skips; their derived knowledge goes with them.
    /// </summary>
    private void RetireExcluded()
    {
        int retired = 0;
        foreach ((Guid sourceId, string locator, string _) in sources.ActiveFileLocators())
        {
            if (IntakeIgnoreRules.IsAlwaysExcluded(root, locator) && sources.Revoke(sourceId))
            {
                retired++;
            }
        }

        if (retired > 0)
        {
            report($"Retired {retired} sources from version-control, dependency or secrets files.");
        }
    }

    internal async Task<WatchedScanResult> ReconcileAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        ApplyInvalidations();
        if (!locatorsNormalized)
        {
            // Before the first scan, so canonical paths match existing sources instead of
            // being captured again under a second identity.
            new SourceLocatorCanonicalizer(sources, report).Run();
            RetireExcluded();
            locatorsNormalized = true;
        }

        HashSet<string> unseen = verified.Keys.Concat(observations.Keys).Concat(status.FileErrorPaths()).ToHashSet(StringComparer.Ordinal);
        var (files, skippedLinks, totalFiles, nextCursor) = intake.InspectWatchedFolder(
            root, pageSize, MaximumDepth, MaximumFolderBytes, cancellationToken, scanCursor, path => unseen.Remove(path));
        foreach (string missing in unseen)
        {
            verified.Remove(missing);
            observations.Remove(missing);
            status.ClearFileError(missing);
        }
        long renameHashBytes = await renames.ReconcileAsync(files, HashBudgetPerScan, cancellationToken);

        int imported = 0;
        TimeSpan? nextScanDelay = null;
        long remainingHashBytes = HashBudgetPerScan - renameHashBytes;
        foreach (ObservedFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IntakeFileRules.IsTemporaryName(file.SourcePath))
            {
                status.ClearFileError(file.SourcePath);
                continue;
            }

            if (file.Length > IntakeFileRules.MaximumFileBytes)
            {
                observations.Remove(file.SourcePath);
                verified.Remove(file.SourcePath);
                RecordFileError(file.SourcePath, "The file exceeds the intake byte limit.");
                continue;
            }

            FileStamp stamp = new(file.Length, file.LastWriteUtc);
            if (!observations.TryGetValue(file.SourcePath, out ObservedState? observed) || observed.Stamp != stamp)
            {
                if (verified.TryGetValue(file.SourcePath, out VerifiedState? known) && known.Stamp == stamp)
                {
                    observed = new ObservedState(stamp, now - quietPeriod);
                    observations[file.SourcePath] = observed;
                }
                else
                {
                    observations[file.SourcePath] = new ObservedState(stamp, now);
                    verified.Remove(file.SourcePath);
                    nextScanDelay = Earlier(nextScanDelay, quietPeriod);
                    continue;
                }
            }

            TimeSpan elapsed = now - observed.FirstSeen;
            if (elapsed < quietPeriod)
            {
                nextScanDelay = Earlier(nextScanDelay, quietPeriod - elapsed);
                continue;
            }

            if (verified.TryGetValue(file.SourcePath, out VerifiedState? previous) && previous.Stamp == stamp && now - previous.LastVerified < FullVerificationInterval)
            {
                continue;
            }
            if (file.Length > remainingHashBytes)
            {
                continue;
            }

            remainingHashBytes -= file.Length;
            if (await TryImportAsync(file, stamp, now, cancellationToken))
            {
                imported++;
            }
        }

        scanCursor = nextCursor;
        if (verified.Count > MaximumCachedFiles)
        {
            foreach (string path in verified.OrderBy(entry => entry.Value.LastVerified).Take(verified.Count - MaximumCachedFiles).Select(entry => entry.Key).ToArray())
            {
                verified.Remove(path);
            }
        }
        if (observations.Count > MaximumCachedFiles)
        {
            foreach (string path in observations.OrderBy(entry => entry.Value.FirstSeen).Take(observations.Count - MaximumCachedFiles).Select(entry => entry.Key).ToArray())
            {
                observations.Remove(path);
            }
        }

        return new WatchedScanResult(totalFiles, imported, nextScanDelay, skippedLinks);
    }

    private static TimeSpan Earlier(TimeSpan? current, TimeSpan candidate) =>
        current is { } prior && prior < candidate ? prior : candidate;

    private async Task<bool> TryImportAsync(ObservedFile file, FileStamp stamp, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            if (!IntakeFileRules.IsStable(file))
            {
                observations.Remove(file.SourcePath);
                return false;
            }

            string digest = await IntakeFileRules.HashAsync(file.SourcePath, cancellationToken);
            if (!IntakeFileRules.IsStable(file))
            {
                observations.Remove(file.SourcePath);
                return false;
            }

            SourceFileVersion? latest = sources.FindLatestFile(file.SourcePath);
            if (latest?.Status == "revoked")
            {
                throw new InvalidOperationException("Revoked sources require explicit owner authorization to reimport.");
            }

            bool capturedRevision = false;
            if (latest?.Sha256 != digest)
            {
                string requestKey = MakeRequestKey(file.SourcePath, latest?.Revision ?? 0, digest);
                CapturedOriginal captured = await intake.ImportWatchedFileAsync(file.SourcePath, root, IntakeFileRules.MaximumFileBytes, requestKey, digest, file.Length, cancellationToken);
                if (captured.Sha256 != digest || captured.Length != file.Length || !IntakeFileRules.IsStable(file))
                {
                    throw new InvalidDataException("The source changed during capture; a new scan is required.");
                }

                report("Captured source revision " + captured.Revision + ".");
                capturedRevision = true;
            }

            verified[file.SourcePath] = new VerifiedState(stamp, now);
            status.ClearFileError(file.SourcePath);
            return capturedRevision;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            RecordFileError(file.SourcePath, exception.Message);
            return false;
        }
    }

    private void RecordFileError(string path, string message)
    {
        if (status.RecordFileError(path, message))
        {
            report("Intake file error; inspect owner-local intake status.");
        }
    }

    private string MakeRequestKey(string path, int previousRevision, string digest) =>
        "watch-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"praxis-watched-file-v2\n{path}\n{sources.CountMovesFrom(path)}\n{previousRevision}\n{digest}")));
}
