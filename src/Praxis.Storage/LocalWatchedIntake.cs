using System.Security.Cryptography;
using System.Text;

namespace Praxis.Storage;

public sealed partial class LocalWatchedIntake
{
    private const int MaximumFiles = 5_000;
    private const int MaximumDepth = 12;
    private const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumFolderBytes = 256L * 1024 * 1024 * 1024;
    private const int ProcessingBudgetPerScan = 64;
    private static readonly TimeSpan FullVerificationInterval = TimeSpan.FromMinutes(5);

    private readonly LocalCatalog catalog;
    private readonly LocalSourceIntake intake;
    private readonly LocalTextProcessor processor;
    private readonly string root;
    private readonly TimeSpan quietPeriod;
    private readonly TimeSpan rescanInterval;
    private readonly Action<string> report;
    private readonly Dictionary<string, ObservedState> observations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, VerifiedState> verified =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> errors =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim scanLock = new(1, 1);

    private sealed record FileStamp(long Length, DateTime LastWriteUtc);
    private sealed record ObservedState(FileStamp Stamp, DateTimeOffset FirstSeen);
    private sealed record VerifiedState(FileStamp Stamp, DateTimeOffset LastVerified);

    public LocalWatchedIntake(
        LocalCatalog catalog, string intakeDirectory,
        TimeSpan? quietPeriod = null, TimeSpan? rescanInterval = null,
        Action<string>? status = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(intakeDirectory);
        if (!Path.IsPathFullyQualified(intakeDirectory))
        {
            throw new ArgumentException("The intake directory must be absolute.", nameof(intakeDirectory));
        }

        this.quietPeriod = quietPeriod ?? TimeSpan.FromSeconds(3);
        this.rescanInterval = rescanInterval ?? TimeSpan.FromSeconds(10);
        if (this.quietPeriod < TimeSpan.Zero ||
            this.rescanInterval < TimeSpan.FromMilliseconds(100))
        {
            throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        }

        this.catalog = catalog;
        intake = new LocalSourceIntake(catalog);
        processor = new LocalTextProcessor(catalog);
        root = Path.GetFullPath(intakeDirectory);
        report = status ?? (_ => { });
    }

    public async Task<int> ReconcileAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await scanLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = intake.InspectFolder(root, MaximumFiles, MaximumDepth,
                MaximumFolderBytes, cancellationToken);
            var present = snapshot.Select(file => file.SourcePath)
                .ToHashSet(StringComparer.Ordinal);
            var removed = observations.Keys.Where(path => !present.Contains(path)).ToArray();
            foreach (var missing in removed)
            {
                observations.Remove(missing);
                verified.Remove(missing);
                errors.Remove(missing);
            }

            if (removed.Length != 0)
            {
                report($"No longer observed: {removed.Length} paths; captured originals are preserved.");
            }

            var imported = 0;
            foreach (var file in snapshot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsTemporaryName(file.SourcePath))
                {
                    continue;
                }

                var stamp = new FileStamp(file.Length, file.LastWriteUtc);
                if (!observations.TryGetValue(file.SourcePath, out var observed) ||
                    observed.Stamp != stamp)
                {
                    observations[file.SourcePath] = new ObservedState(stamp, now);
                    verified.Remove(file.SourcePath);
                    continue;
                }

                if (now - observed.FirstSeen < quietPeriod ||
                    (verified.TryGetValue(file.SourcePath, out var previous) &&
                     previous.Stamp == stamp &&
                     now - previous.LastVerified < FullVerificationInterval))
                {
                    continue;
                }

                try
                {
                    if (file.Length > MaximumFileBytes)
                    {
                        throw new InvalidDataException("The file exceeds the intake byte limit.");
                    }

                    if (!IsStable(file))
                    {
                        observations.Remove(file.SourcePath);
                        continue;
                    }

                    var digest = await HashSourceAsync(file.SourcePath, cancellationToken);
                    if (!IsStable(file))
                    {
                        observations.Remove(file.SourcePath);
                        continue;
                    }

                    var latest = catalog.FindLatestFile(file.SourcePath);
                    if (latest?.Status == "revoked")
                    {
                        throw new InvalidOperationException(
                            "Revoked sources require explicit owner authorization to reimport.");
                    }

                    if (latest?.Sha256 != digest)
                    {
                        var requestKey = MakeRequestKey(
                            file.SourcePath, latest?.Revision ?? 0, digest);
                        var captured = await intake.ImportFileAsync(
                            file.SourcePath, MaximumFileBytes, requestKey, cancellationToken);
                        if (captured.Sha256 != digest || captured.Length != file.Length ||
                            !IsStable(file))
                        {
                            throw new InvalidDataException(
                                "The source changed during capture; a new scan is required.");
                        }

                        imported++;
                        report("Captured source revision " + captured.Revision +
                            ": " + file.SourcePath);
                    }

                    verified[file.SourcePath] = new VerifiedState(stamp, now);
                    errors.Remove(file.SourcePath);
                }
                catch (IOException exception)
                {
                    ReportError(file.SourcePath, exception.Message);
                }
                catch (UnauthorizedAccessException exception)
                {
                    ReportError(file.SourcePath, exception.Message);
                }
                catch (InvalidDataException exception)
                {
                    ReportError(file.SourcePath, exception.Message);
                }
                catch (InvalidOperationException exception)
                {
                    ReportError(file.SourcePath, exception.Message);
                }
            }

            for (var index = 0; index < ProcessingBudgetPerScan; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await processor.ProcessNextAsync(cancellationToken))
                {
                    break;
                }
            }

            return imported;
        }
        finally
        {
            scanLock.Release();
        }
    }

    private void ReportError(string path, string message)
    {
        if (!errors.TryGetValue(path, out var previous) || previous != message)
        {
            report("Intake error: " + path + ": " + message);
            errors[path] = message;
        }
    }

    private static bool IsTemporaryName(string path)
    {
        var name = Path.GetFileName(path);
        return name is ".DS_Store" or ".gitignore" ||
            name.EndsWith('~') ||
            new[] { ".tmp", ".part", ".crdownload", ".download", ".swp", ".swx" }
                .Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStable(LocalObservedFile file)
    {
        var info = new FileInfo(file.SourcePath);
        return info.Exists &&
               (info.Attributes & FileAttributes.ReparsePoint) == 0 &&
               info.Length == file.Length &&
               info.LastWriteTimeUtc == file.LastWriteUtc;
    }

    private static async Task<string> HashSourceAsync(
        string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string MakeRequestKey(string path, int previousRevision, string digest) =>
        "watch-" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"praxis-watched-file-v1\n{path}\n{previousRevision}\n{digest}")));
}
