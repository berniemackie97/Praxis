using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

internal sealed record LocalSnapshotObject(string Digest, long Length);

internal sealed record LocalSnapshotManifest(
    int FormatVersion, int CatalogVersion, string CatalogSha256,
    long CatalogLength, List<LocalSnapshotObject> Objects);

/// <summary>Snapshot layout, manifest and private-file primitives shared by backup and restore.</summary>
internal static class SnapshotFiles
{
    internal const int FormatVersion = 1;
    internal const long MaximumManifestBytes = 32 * 1024 * 1024;
    internal const int MaximumObjects = 100_000;
    internal static readonly UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal static readonly UnixFileMode PrivateFile =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static List<LocalSnapshotObject> ReadSnapshotObjects(string catalogPath, int catalogVersion)
    {
        using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalogPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        snapshot.Open();
        CatalogSchema.VerifyIntegrity(snapshot);
        if (CatalogSchema.ReadVersion(snapshot) != catalogVersion)
        {
            throw new NotSupportedException("The snapshot catalog schema is not supported.");
        }

        CatalogSchema.VerifySchema(snapshot, catalogVersion);
        using (var receipts = snapshot.CreateCommand("""
            SELECT COUNT(*) FROM ingest_requests r
            LEFT JOIN items i ON i.source_id = r.source_id
            LEFT JOIN item_versions v ON v.item_id = i.item_id AND v.revision = r.revision
            WHERE r.state = 'committed' AND r.revision IS NOT NULL AND v.item_id IS NULL
            """))
        {
            if (Convert.ToInt64(receipts.ExecuteScalar()) != 0)
            {
                throw new InvalidDataException("The snapshot has an unresolved committed receipt.");
            }
        }

        var originals = new List<LocalSnapshotObject>();
        using var command = snapshot.CreateCommand(
            "SELECT digest, byte_length FROM original_objects ORDER BY digest");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (originals.Count == MaximumObjects)
            {
                throw new InvalidDataException("The snapshot exceeds the supported object count.");
            }

            var digest = reader.GetString(0);
            var length = reader.GetInt64(1);
            if (!OriginalFileStore.IsValidDigest(digest) || length < 0)
            {
                throw new InvalidDataException("The snapshot contains invalid original metadata.");
            }

            originals.Add(new LocalSnapshotObject(digest, length));
        }

        return originals;
    }

    internal static LocalSnapshotManifest ReadSnapshotManifest(string backup)
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

    internal static string ValidateExistingBackup(string backupDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        if (!Path.IsPathFullyQualified(backupDirectory))
        {
            throw new ArgumentException("The backup path must be absolute.", nameof(backupDirectory));
        }

        var root = CheckPrivateDirectory(Path.GetFullPath(backupDirectory));
        root = CheckPrivateDirectory(CatalogPaths.ResolveExisting(root));
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

    internal static string ValidateNewDestination(string path, string disallowedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The destination must be absolute.", nameof(path));
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.GetDirectoryName(full)
            ?? throw new ArgumentException("A destination parent is required.", nameof(path));
        CheckPrivateDirectory(parent);
        var resolvedParent = CheckPrivateDirectory(CatalogPaths.ResolveExisting(parent));
        var destination = Path.Combine(resolvedParent, Path.GetFileName(full));
        if (CatalogPaths.Contains(CatalogPaths.ResolveExisting(disallowedRoot), destination))
        {
            throw new InvalidOperationException("The destination cannot be inside the source.");
        }

        if (File.Exists(destination) || Directory.Exists(destination) ||
            new DirectoryInfo(destination).LinkTarget is not null)
        {
            throw new IOException("The destination already exists.");
        }

        return destination;
    }

    internal static string CreateStagingDirectory(string destination)
    {
        var parent = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileName(destination);
        var staging = Path.Combine(parent, $".{name}.incomplete-{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(staging);
        }
        else
        {
            Directory.CreateDirectory(staging, PrivateDirectory);
        }

        CheckPrivateDirectory(staging);
        return staging;
    }

    internal static string CheckPrivateDirectory(string path)
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

    internal static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, PrivateFile);
        }
    }

    internal static FileStream OpenNewPrivateFile(string path)
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

    internal static async Task CopyCheckedFileAsync(
        string source, string target, long length, string digest,
        CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The snapshot contains a linked file.");
        }

        await using (var input = new FileStream(source, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous))
        {
            if (input.Length != length)
            {
                throw new InvalidDataException("The snapshot catalog length has changed.");
            }

            await using var output = OpenNewPrivateFile(target);
            var buffer = new byte[64 * 1024];
            for (var remaining = length; remaining > 0;)
            {
                var count = (int)Math.Min(remaining, buffer.Length);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                remaining -= count;
            }

            if (input.ReadByte() != -1)
            {
                throw new InvalidDataException("The snapshot catalog length has changed.");
            }

            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        var (_, copiedHash) = await HashFileAsync(target, cancellationToken, length);
        if (copiedHash != digest)
        {
            throw new InvalidDataException("The copied snapshot catalog failed verification.");
        }
    }

    internal static async Task<(long Length, string Digest)> HashFileAsync(
        string path, CancellationToken cancellationToken, long? expectedLength = null)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The snapshot contains a linked file.");
        }

        await using var input = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = input.Length;
        if (expectedLength is { } expected && length != expected)
        {
            throw new InvalidDataException("The snapshot catalog length has changed.");
        }

        var digest = await SHA256.HashDataAsync(input, cancellationToken);
        if (input.Length != length)
        {
            throw new InvalidDataException("The snapshot catalog length has changed.");
        }

        return (length, Convert.ToHexStringLower(digest));
    }

    internal static bool IsSnapshotSchemaVersion(int version) =>
        version is >= 2 and <= CatalogSchema.CurrentVersion;

    internal static void EnsureCatalogFitsBudget(SqliteConnection database, long maxBytes)
    {
        using var count = database.CreateCommand("PRAGMA page_count");
        using var size = database.CreateCommand("PRAGMA page_size");
        var pageCount = Convert.ToInt64(count.ExecuteScalar());
        var pageSize = Convert.ToInt64(size.ExecuteScalar());
        if (pageSize <= 0 || pageCount > maxBytes / pageSize)
        {
            throw new InvalidDataException("The snapshot catalog exceeds the byte budget.");
        }
    }

    internal static long EnforceBudget(
        long catalogLength, IReadOnlyList<LocalSnapshotObject> objects, long maxBytes)
    {
        if (catalogLength > maxBytes)
        {
            throw new InvalidDataException("The snapshot exceeds its byte budget.");
        }

        var remaining = maxBytes - catalogLength;
        foreach (var item in objects)
        {
            if (item is null || item.Length < 0 || item.Length > remaining)
            {
                throw new InvalidDataException("The snapshot exceeds its byte budget.");
            }

            remaining -= item.Length;
        }

        return maxBytes - remaining;
    }

    internal static void ValidateBudget(long maxBytes)
    {
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }
    }

    internal static async Task<LocalSnapshotReport> VerifyContentsAsync(
        string directory, LocalSnapshotManifest manifest, long maxBytes,
        CancellationToken cancellationToken)
    {
        if (manifest.FormatVersion != FormatVersion ||
            !IsSnapshotSchemaVersion(manifest.CatalogVersion) ||
            manifest.Objects is null ||
            manifest.Objects.Count > MaximumObjects ||
            !OriginalFileStore.IsValidDigest(manifest.CatalogSha256) || manifest.CatalogLength < 0)
        {
            throw new InvalidDataException("The snapshot manifest is incompatible.");
        }

        var total = EnforceBudget(manifest.CatalogLength, manifest.Objects, maxBytes);
        var catalogPath = Path.Combine(directory, "catalog.sqlite");
        var (_, hash) = await HashFileAsync(catalogPath, cancellationToken, manifest.CatalogLength);
        if (hash != manifest.CatalogSha256)
        {
            throw new InvalidDataException("The snapshot catalog failed integrity verification.");
        }

        var objects = ReadSnapshotObjects(catalogPath, manifest.CatalogVersion);
        if (!objects.SequenceEqual(manifest.Objects))
        {
            throw new InvalidDataException("The manifest does not match the catalog objects.");
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
}
