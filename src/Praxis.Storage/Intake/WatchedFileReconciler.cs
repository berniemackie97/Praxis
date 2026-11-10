using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Praxis.Storage.Catalog;
using Praxis.Storage.Originals;
using Praxis.Storage.Sources;

namespace Praxis.Storage.Intake;

internal sealed record WatchedScanResult(int ObservedFiles, int Imported);

/// <summary>
/// One reconciliation pass over the approved folder: applies observed renames, waits for each
/// file to stay unchanged for the quiet period, then captures new content as a revision with a
/// deterministic request key. Unchanged verified files are re-hashed only periodically.
/// Not thread-safe; the owning intake serializes passes.
/// </summary>
internal sealed class WatchedFileReconciler(LocalCatalog catalog, LocalSourceIntake intake, string root, TimeSpan quietPeriod, ObservedRenameCorrelator renames, IntakeStatusTracker status, Action<string> report)
{
    private const int MaximumFiles = 5_000;
    private const int MaximumDepth = 12;
    private const long MaximumFolderBytes = 256L * 1024 * 1024 * 1024;
    private const long HashBudgetPerScan = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan FullVerificationInterval = TimeSpan.FromMinutes(30);
    private bool locatorsNormalized;

    private readonly Dictionary<string, ObservedState> observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VerifiedState> verified = new(StringComparer.Ordinal);

    private sealed record FileStamp(long Length, DateTime LastWriteUtc);
    private sealed record ObservedState(FileStamp Stamp, DateTimeOffset FirstSeen);
    private sealed record VerifiedState(FileStamp Stamp, DateTimeOffset LastVerified);

    internal async Task<WatchedScanResult> ReconcileAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!locatorsNormalized)
        {
            // Before the first scan, so canonical paths match existing sources instead of
            // being captured again under a second identity.
            new SourceLocatorCanonicalizer(catalog, report).Run();
            locatorsNormalized = true;
        }

        IReadOnlyList<LocalObservedFile> files = intake.InspectFolder(root, MaximumFiles, MaximumDepth, MaximumFolderBytes, cancellationToken);
        long renameHashBytes = await renames.ReconcileAsync(files, HashBudgetPerScan, cancellationToken);
        ForgetMissing(files);

        int imported = 0;
        long remainingHashBytes = HashBudgetPerScan - renameHashBytes;
        foreach (LocalObservedFile file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IntakeFileRules.IsTemporaryName(file.SourcePath))
            {
                continue;
            }

            FileStamp stamp = new(file.Length, file.LastWriteUtc);
            if (!observations.TryGetValue(file.SourcePath, out ObservedState? observed) || observed.Stamp != stamp)
            {
                observations[file.SourcePath] = new ObservedState(stamp, now);
                verified.Remove(file.SourcePath);
                continue;
            }

            if (now - observed.FirstSeen < quietPeriod || (verified.TryGetValue(file.SourcePath, out VerifiedState? previous) && previous.Stamp == stamp && now - previous.LastVerified < FullVerificationInterval) || file.Length > remainingHashBytes)
            {
                continue;
            }

            remainingHashBytes -= file.Length;
            if (await TryImportAsync(file, stamp, now, cancellationToken))
            {
                imported++;
            }
        }

        return new WatchedScanResult(files.Count, imported);
    }

    private void ForgetMissing(IReadOnlyList<LocalObservedFile> files)
    {
        HashSet<string> present = files.Select(file => file.SourcePath).ToHashSet(StringComparer.Ordinal);
        string[] removed = observations.Keys.Where(path => !present.Contains(path)).ToArray();
        foreach (string missing in removed)
        {
            observations.Remove(missing);
            verified.Remove(missing);
            status.ClearFileError(missing);
        }

        if (removed.Length != 0)
        {
            report($"No longer observed: {removed.Length} paths; captured originals are preserved.");
        }
    }

    private async Task<bool> TryImportAsync(LocalObservedFile file, FileStamp stamp, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            if (file.Length > IntakeFileRules.MaximumFileBytes)
            {
                throw new InvalidDataException("The file exceeds the intake byte limit.");
            }

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

            LocalFileVersion? latest = catalog.Sources.FindLatestFile(file.SourcePath);
            if (latest?.Status == "revoked")
            {
                throw new InvalidOperationException("Revoked sources require explicit owner authorization to reimport.");
            }

            bool capturedRevision = false;
            if (latest?.Sha256 != digest)
            {
                string requestKey = MakeRequestKey(file.SourcePath, latest?.Revision ?? 0, digest);
                CapturedOriginal captured = await intake.ImportFileAsync(file.SourcePath, IntakeFileRules.MaximumFileBytes, requestKey, cancellationToken);
                if (captured.Sha256 != digest || captured.Length != file.Length || !IntakeFileRules.IsStable(file))
                {
                    throw new InvalidDataException("The source changed during capture; a new scan is required.");
                }

                report("Captured source revision " + captured.Revision +
                    ": " + JsonSerializer.Serialize(file.SourcePath));
                capturedRevision = true;
            }

            verified[file.SourcePath] = new VerifiedState(stamp, now);
            status.ClearFileError(file.SourcePath);
            return capturedRevision;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            if (status.RecordFileError(file.SourcePath, exception.Message))
            {
                report("Intake error: " + JsonSerializer.Serialize(file.SourcePath) +
                    ": " + JsonSerializer.Serialize(exception.Message));
            }

            return false;
        }
    }

    private string MakeRequestKey(string path, int previousRevision, string digest) =>
        "watch-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"praxis-watched-file-v2\n{path}\n{catalog.Sources.CountMovesFrom(path)}\n{previousRevision}\n{digest}")));
}
