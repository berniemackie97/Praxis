using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

public sealed record LocalSnapshotReport(int ObjectCount, long TotalBytes, string CatalogSha256);

internal sealed record LocalSnapshotObject(string Digest, long Length);

internal sealed record LocalSnapshotManifest(
    int FormatVersion, int CatalogVersion, string CatalogSha256,
    long CatalogLength, List<LocalSnapshotObject> Objects);

public sealed partial class LocalCatalog
{
    private const int SnapshotFormatVersion = 1;
    private const long MaximumManifestBytes = 32 * 1024 * 1024;
    private const int MaximumSnapshotObjects = 100_000;
    private static readonly UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode PrivateFile =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public async Task<LocalSnapshotReport> BackupAsync(
        string destinationDirectory, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetDirectoryName(DatabasePath)!;
        ValidateRoot(root);
        var destination = ValidateNewDestination(destinationDirectory, root);
        ValidateBudget(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var staging = CreateStagingDirectory(destination);
        var catalogPath = Path.Combine(staging, "catalog.sqlite");
        using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalogPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            snapshot.Open();
            lock (connection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                connection.BackupDatabase(snapshot);
            }

            using var journal = Command(snapshot, "PRAGMA journal_mode = DELETE");
            if (!string.Equals(journal.ExecuteScalar() as string,
                "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The snapshot could not be finalized.");
            }
        }

        RestrictFile(catalogPath);
        var objects = ReadSnapshotObjects(catalogPath);
        var (catalogLength, catalogDigest) =
            await HashFileAsync(catalogPath, cancellationToken);
        var totalBytes = EnforceBudget(catalogLength, objects, maxBytes);
        var source = new OriginalFileStore(root);
        var target = new OriginalFileStore(staging);
        foreach (var original in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await source.CopyVerifiedAsync(
                new OriginalObject(original.Digest, original.Length),
                target, cancellationToken);
        }

        var manifest = new LocalSnapshotManifest(
            SnapshotFormatVersion, CurrentSchemaVersion,
            catalogDigest, catalogLength, objects);
        var manifestPath = Path.Combine(staging, "manifest.json");
        await using (var output = OpenNewPrivateFile(manifestPath))
        {
            await JsonSerializer.SerializeAsync(output, manifest,
                cancellationToken: cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        var verified = await VerifyBackupAsync(staging, maxBytes, cancellationToken);
        Directory.Move(staging, destination);
        return verified with { TotalBytes = totalBytes };
    }

    public static async Task<LocalSnapshotReport> RestoreAsync(
        string backupDirectory, string destinationDirectory, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var backup = ValidateExistingBackup(backupDirectory);
        var destination = ValidateNewDestination(destinationDirectory, backup);
        ValidateBudget(maxBytes);
        var verified = await VerifyBackupAsync(backup, maxBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var staging = CreateStagingDirectory(destination);
        var expected = ReadSnapshotManifest(backup);
        await CopyCheckedFileAsync(
            Path.Combine(backup, "catalog.sqlite"),
            Path.Combine(staging, "catalog.sqlite"),
            expected.CatalogLength, expected.CatalogSha256, cancellationToken);

        var source = new OriginalFileStore(backup);
        var target = new OriginalFileStore(staging);
        foreach (var original in expected.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await source.CopyVerifiedAsync(
                new OriginalObject(original.Digest, original.Length),
                target, cancellationToken);
        }

        await VerifySnapshotContentsAsync(staging, expected, maxBytes, cancellationToken);
        using (var restored = Open(staging))
        {
            var inspection = await restored.InspectRecoveryAsync(cancellationToken);
            if (inspection.MissingObjects != 0 || inspection.CorruptObjects != 0)
            {
                throw new InvalidDataException("The restored catalog has invalid originals.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(staging, destination);
        return verified;
    }

    public static async Task<LocalSnapshotReport> VerifyBackupAsync(
        string backupDirectory, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var backup = ValidateExistingBackup(backupDirectory);
        ValidateBudget(maxBytes);
        var manifest = ReadSnapshotManifest(backup);
        return await VerifySnapshotContentsAsync(
            backup, manifest, maxBytes, cancellationToken);
    }

    private static async Task<LocalSnapshotReport> VerifySnapshotContentsAsync(
        string directory, LocalSnapshotManifest manifest, long maxBytes,
        CancellationToken cancellationToken)
    {
        if (manifest.FormatVersion != SnapshotFormatVersion ||
            manifest.CatalogVersion != CurrentSchemaVersion ||
            manifest.Objects is null ||
            manifest.Objects.Count > MaximumSnapshotObjects ||
            !IsDigest(manifest.CatalogSha256) || manifest.CatalogLength < 0)
        {
            throw new InvalidDataException("The snapshot manifest is incompatible.");
        }

        var objects = ReadSnapshotObjects(Path.Combine(directory, "catalog.sqlite"));
        if (!objects.SequenceEqual(manifest.Objects))
        {
            throw new InvalidDataException("The manifest does not match the catalog objects.");
        }

        var total = EnforceBudget(manifest.CatalogLength, objects, maxBytes);
        var (length, hash) = await HashFileAsync(
            Path.Combine(directory, "catalog.sqlite"), cancellationToken);
        if (length != manifest.CatalogLength || hash != manifest.CatalogSha256)
        {
            throw new InvalidDataException("The snapshot catalog failed integrity verification.");
        }

        var store = new OriginalFileStore(directory);
        var published = store.EnumeratePublishedDigests().ToHashSet(StringComparer.Ordinal);
        if (published.Count != objects.Count ||
            objects.Any(item => !published.Contains(item.Digest)))
        {
            throw new InvalidDataException("The snapshot object inventory is incomplete or unexpected.");
        }

        foreach (var item in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.VerifyAsync(
                new OriginalObject(item.Digest, item.Length), cancellationToken);
        }

        return new LocalSnapshotReport(objects.Count, total, hash);
    }

    private static List<LocalSnapshotObject> ReadSnapshotObjects(string catalogPath)
    {
        using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalogPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        snapshot.Open();
        VerifyIntegrity(snapshot);
        if (Convert.ToInt32(Scalar(snapshot, "PRAGMA user_version")) != CurrentSchemaVersion)
        {
            throw new NotSupportedException("The snapshot catalog schema is not supported.");
        }

        VerifySchema(snapshot, CurrentSchemaVersion);
        using (var receipts = Command(snapshot, """
            SELECT COUNT(*) FROM ingest_requests r
            JOIN items i ON i.source_id = r.source_id
            LEFT JOIN item_versions v ON v.item_id = i.item_id AND v.revision = r.revision
            WHERE r.state = 'committed' AND v.item_id IS NULL
            """))
        {
            if (Convert.ToInt64(receipts.ExecuteScalar()) != 0)
            {
                throw new InvalidDataException("The snapshot has an unresolved committed receipt.");
            }
        }

        var originals = new List<LocalSnapshotObject>();
        using var command = Command(snapshot,
            "SELECT digest, byte_length FROM original_objects ORDER BY digest");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (originals.Count == MaximumSnapshotObjects)
            {
                throw new InvalidDataException("The snapshot exceeds the supported object count.");
            }

            var digest = reader.GetString(0);
            var length = reader.GetInt64(1);
            if (!IsDigest(digest) || length < 0)
            {
                throw new InvalidDataException("The snapshot contains invalid original metadata.");
            }

            originals.Add(new LocalSnapshotObject(digest, length));
        }

        return originals;
    }

    private static LocalSnapshotManifest ReadSnapshotManifest(string backup)
    {
        var path = Path.Combine(backup, "manifest.json");
        var length = new FileInfo(path).Length;
        if (length is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("The snapshot manifest size is invalid.");
        }

        using var input = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read);
        try
        {
            return JsonSerializer.Deserialize<LocalSnapshotManifest>(input)
                ?? throw new InvalidDataException("The snapshot manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The snapshot manifest is malformed.", exception);
        }
    }

    private static string ValidateExistingBackup(string backupDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        if (!Path.IsPathFullyQualified(backupDirectory))
        {
            throw new ArgumentException("The backup path must be absolute.", nameof(backupDirectory));
        }

        var root = Path.GetFullPath(backupDirectory);
        CheckPrivateDirectory(root);
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "catalog.sqlite", "manifest.json", "objects"
        };
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (!allowed.Contains(Path.GetFileName(entry)) ||
                (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The backup contains an unexpected or linked entry.");
            }
        }

        foreach (var file in new[] { "catalog.sqlite", "manifest.json" })
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The backup is incomplete.");
            }
        }

        return root;
    }

    private static string ValidateNewDestination(string path, string disallowedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The destination must be absolute.", nameof(path));
        }

        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full)
            ?? throw new ArgumentException("A destination parent is required.", nameof(path));
        CheckPrivateDirectory(parent);
        var relative = Path.GetRelativePath(disallowedRoot, full);
        if (relative == "." ||
            (!Path.IsPathRooted(relative) && relative != ".." &&
             !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
             !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The destination cannot be inside the source.");
        }

        if (File.Exists(full) || Directory.Exists(full))
        {
            throw new IOException("The destination already exists.");
        }

        return full;
    }

    private static string CreateStagingDirectory(string destination)
    {
        var parent = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileName(destination);
        var staging = Path.Combine(parent, $".{name}.incomplete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging, PrivateDirectory);
        CheckPrivateDirectory(staging);
        return staging;
    }

    private static string CheckPrivateDirectory(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            !Directory.Exists(path))
        {
            throw new InvalidDataException("The directory must exist and cannot be linked.");
        }

        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(path) & ~PrivateDirectory) != 0)
        {
            throw new UnauthorizedAccessException("Snapshot directories must be owner-private.");
        }

        return path;
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateFile);
        }
    }

    private static FileStream OpenNewPrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFile;
        }

        return new FileStream(path, options);
    }

    private static async Task CopyCheckedFileAsync(
        string source, string target, long length, string digest,
        CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The snapshot contains a linked file.");
        }

        await using (var input = new FileStream(source, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous))
        await using (var output = OpenNewPrivateFile(target))
        {
            await input.CopyToAsync(output, 64 * 1024, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        var (copiedLength, copiedHash) = await HashFileAsync(target, cancellationToken);
        if (copiedLength != length || copiedHash != digest)
        {
            throw new InvalidDataException("The copied snapshot catalog failed verification.");
        }
    }

    private static async Task<(long Length, string Digest)> HashFileAsync(
        string path, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The snapshot contains a linked file.");
        }

        await using var input = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return (input.Length, Convert.ToHexStringLower(
            await SHA256.HashDataAsync(input, cancellationToken)));
    }

    private static long EnforceBudget(
        long catalogLength, IReadOnlyList<LocalSnapshotObject> objects, long maxBytes)
    {
        if (catalogLength > maxBytes)
        {
            throw new InvalidDataException("The snapshot exceeds its byte budget.");
        }

        var remaining = maxBytes - catalogLength;
        foreach (var item in objects)
        {
            if (item.Length < 0 || item.Length > remaining)
            {
                throw new InvalidDataException("The snapshot exceeds its byte budget.");
            }

            remaining -= item.Length;
        }

        return maxBytes - remaining;
    }

    private static void ValidateBudget(long maxBytes)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
    }

    private static bool IsDigest(string? digest) =>
        digest is { Length: 64 } && digest.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
