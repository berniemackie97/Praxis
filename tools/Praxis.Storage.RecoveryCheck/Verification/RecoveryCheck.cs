using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Storage;

namespace Praxis.Storage.RecoveryCheck;

internal sealed record RecoveryEvidence(
    long OriginalBytes,
    Guid SourceId,
    Guid ItemId,
    int Revision,
    string OriginalSha256,
    Guid SmallItemId,
    string BackupCatalogSha256,
    int BackupObjectCount,
    long BackupTotalBytes,
    DateTimeOffset CreatedUtc);

internal static class RecoveryCheck
{
    private const long LargeBytes = 2L * 1024 * 1024 * 1024 + 4096;
    private const long Budget = LargeBytes + 128L * 1024 * 1024;
    private const string EvidenceFile = "evidence.json";
    private const string LargeRequestKey = "physical-large-v1";

    public static async Task<string> CreateAsync(
        string scratchRoot, CancellationToken cancellationToken)
    {
        var root = RequireExistingDirectory(scratchRoot);
        EnsureCapacity(root);
        var workspace = Path.Combine(root, "praxis-recovery-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(workspace);
        var livePath = Path.Combine(workspace, "live");
        CreatePrivateDirectory(livePath);
        Console.WriteLine("Physical check workspace: " + workspace);

        CapturedOriginal large;
        CapturedOriginal small;
        LocalSnapshotReport snapshot;
        using (var live = LocalCatalog.Open(livePath))
        {
            await using var source = new GeneratedContentStream(LargeBytes);
            large = await live.Originals.CaptureFileAsync(
                Path.Combine(workspace, "large.bin"), source, LargeBytes,
                LargeRequestKey, cancellationToken);
            using var smallStream = new MemoryStream([1, 2, 3, 4]);
            small = await live.Originals.CaptureFileAsync(
                Path.Combine(workspace, "small.bin"), smallStream, 4,
                "physical-small-v1", cancellationToken);
            snapshot = await live.Recovery.BackupAsync(
                Path.Combine(workspace, "backup"), Budget, cancellationToken);

            using var changed = new MemoryStream([8, 9]);
            var newer = await live.Originals.CaptureFileAsync(
                Path.Combine(workspace, "small.bin"), changed, 2,
                "physical-small-v2", cancellationToken);
            Require(newer.Revision == 2 && newer.ItemId == small.ItemId,
                "The live revision was not recorded.");
        }

        var evidence = new RecoveryEvidence(
            large.Length, large.SourceId, large.ItemId, large.Revision,
            large.Sha256, small.ItemId, snapshot.CatalogSha256,
            snapshot.ObjectCount, snapshot.TotalBytes, DateTimeOffset.UtcNow);

        await LocalCatalogSnapshots.RestoreAsync(
            Path.Combine(workspace, "backup"),
            Path.Combine(workspace, "restored"), Budget, cancellationToken);
        await VerifyWorkspaceAsync(workspace, evidence, cancellationToken);
        await SaveEvidenceAsync(workspace, evidence, cancellationToken);
        return workspace;
    }

    public static async Task VerifyAsync(
        string workspace, CancellationToken cancellationToken)
    {
        var root = RequireExistingDirectory(workspace);
        var path = Path.Combine(root, EvidenceFile);
        if (!File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(path).Length is <= 0 or > 16 * 1024)
        {
            throw new InvalidDataException("The physical check evidence file is invalid.");
        }

        await using var input = File.OpenRead(path);
        var evidence = await JsonSerializer.DeserializeAsync<RecoveryEvidence>(
            input, cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("The physical check evidence is empty.");
        await VerifyWorkspaceAsync(root, evidence, cancellationToken);
    }

    private static async Task VerifyWorkspaceAsync(
        string workspace, RecoveryEvidence evidence, CancellationToken cancellationToken)
    {
        Require(evidence.OriginalBytes == LargeBytes && evidence.Revision == 1 &&
                evidence.SourceId != Guid.Empty && evidence.ItemId != Guid.Empty &&
                evidence.SmallItemId != Guid.Empty && evidence.BackupObjectCount == 2,
            "The physical check evidence does not describe the expected test.");
        var backup = await LocalCatalogSnapshots.VerifyAsync(
            Path.Combine(workspace, "backup"), Budget, cancellationToken);
        Require(backup.ObjectCount == evidence.BackupObjectCount &&
                backup.TotalBytes == evidence.BackupTotalBytes &&
                backup.CatalogSha256 == evidence.BackupCatalogSha256,
            "The backup differs from its recorded verification.");

        using var restored = LocalCatalog.Open(Path.Combine(workspace, "restored"));
        Require(restored.SchemaVersion == 5, "The restored catalog has an unexpected schema.");
        await restored.Originals.VerifyAsync(evidence.ItemId, cancellationToken);
        await restored.Originals.VerifyAsync(evidence.SmallItemId, cancellationToken);

        var receipt = await restored.Originals.CaptureFileAsync(
            Path.Combine(workspace, "large.bin"), Stream.Null, 0,
            LargeRequestKey, cancellationToken);
        Require(receipt.SourceId == evidence.SourceId &&
                receipt.ItemId == evidence.ItemId &&
                receipt.Revision == evidence.Revision &&
                receipt.Sha256 == evidence.OriginalSha256 &&
                receipt.Length == evidence.OriginalBytes,
            "The historical receipt or original changed after restore.");
        Require(ReadRevisionCount(restored.DatabasePath, evidence.SmallItemId) == 1,
            "The restored snapshot included a later live-only revision.");
    }

    private static long ReadRevisionCount(string databasePath, Guid itemId)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM item_versions WHERE item_id = $item";
        query.Parameters.AddWithValue("$item", itemId.ToString("D"));
        return (long)query.ExecuteScalar()!;
    }

    private static async Task SaveEvidenceAsync(
        string workspace, RecoveryEvidence evidence, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(workspace, "evidence.pending");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (var output = new FileStream(temporary, options))
        {
            await JsonSerializer.SerializeAsync(output, evidence,
                cancellationToken: cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        File.Move(temporary, Path.Combine(workspace, EvidenceFile));
    }

    private static string RequireExistingDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Provide an absolute directory path.", nameof(path));
        }

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full) ||
            (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
        {
            throw new DirectoryNotFoundException("The selected directory is unavailable or linked.");
        }

        return full;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void EnsureCapacity(string path)
    {
        var drive = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .OrderByDescending(d => d.RootDirectory.FullName.Length)
            .FirstOrDefault(d => IsWithinDrive(path, d.RootDirectory.FullName));
        if (OperatingSystem.IsMacOS() &&
            path.StartsWith("/Volumes/", StringComparison.Ordinal) &&
            (drive is null ||
             !drive.RootDirectory.FullName.StartsWith("/Volumes/", StringComparison.Ordinal)))
        {
            throw new IOException("The selected external volume is not mounted.");
        }

        var required = checked(LargeBytes * 4 + 1024L * 1024 * 1024);
        if (drive is null || drive.AvailableFreeSpace < required)
        {
            throw new IOException("The selected drive lacks the verified free space for this check.");
        }
    }

    private static bool IsWithinDrive(string path, string driveRoot)
    {
        var relative = Path.GetRelativePath(driveRoot, path);
        return relative == "." || (!Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
