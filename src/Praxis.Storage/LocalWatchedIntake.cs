namespace Praxis.Storage;

public sealed partial class LocalWatchedIntake
{
    private const int MaximumFiles = 5_000;
    private const int MaximumDepth = 12;
    private const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumFolderBytes = 256L * 1024 * 1024 * 1024;
    private const int ProcessingBudgetPerScan = 64;
    private const long HashBudgetPerScan = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan FullVerificationInterval = TimeSpan.FromMinutes(30);

    private readonly LocalCatalog catalog;
    private readonly LocalSourceIntake intake;
    private readonly LocalTextProcessor processor;
    private readonly LocalTopicProposer topics;
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
        if (this.quietPeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(quietPeriod));
        }

        if (this.rescanInterval < TimeSpan.FromMilliseconds(100))
        {
            throw new ArgumentOutOfRangeException(nameof(rescanInterval));
        }

        this.catalog = catalog;
        intake = new LocalSourceIntake(catalog);
        processor = new LocalTextProcessor(catalog);
        topics = new LocalTopicProposer(catalog);
        root = Path.GetFullPath(intakeDirectory);
        report = status ?? (_ => { });
    }

    public async Task<int> ReconcileAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await scanLock.WaitAsync(cancellationToken);
        try
        {
            var files = intake.InspectFolder(root, MaximumFiles, MaximumDepth,
                MaximumFolderBytes, cancellationToken);
            var present = files.Select(file => file.SourcePath)
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
            var remainingHashBytes = HashBudgetPerScan;
            foreach (var file in files)
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

            await ProcessPendingAsync(cancellationToken);
            RecordSuccessfulScan(now, files.Count, imported);
            return imported;
        }
        finally
        {
            scanLock.Release();
        }
    }

    private async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < ProcessingBudgetPerScan; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await processor.ProcessNextAsync(cancellationToken))
            {
                break;
            }
        }

        for (var index = 0; index < 16; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!topics.ProcessNext())
            {
                break;
            }
        }
    }
}
