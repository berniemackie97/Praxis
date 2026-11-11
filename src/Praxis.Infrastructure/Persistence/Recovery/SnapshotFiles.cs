using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Domain.Common;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.FileSystem;

namespace Praxis.Infrastructure.Persistence.Recovery;

/// <summary>Snapshot layout, manifest and private-file primitives shared by backup and restore.</summary>
internal static class SnapshotFiles
{
    internal const int FormatVersion = 1;
    internal const long MaximumManifestBytes = 32 * 1024 * 1024;
    internal const int MaximumObjects = 100_000;
    internal static readonly UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal static readonly UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    internal static readonly string[] sourceArray = new[] { "catalog.sqlite", "manifest.json" };

    internal static List<SnapshotObject> ReadSnapshotObjects(string catalogPath, int catalogVersion)
    {
        using SqliteConnection snapshot = new(new SqliteConnectionStringBuilder
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
        using (SqliteCommand receipts = snapshot.CreateCommand("""
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

        List<SnapshotObject> originals = new();
        using SqliteCommand command = snapshot.CreateCommand("SELECT digest, byte_length FROM original_objects ORDER BY digest");
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (originals.Count == MaximumObjects)
            {
                throw new InvalidDataException("The snapshot exceeds the supported object count.");
            }

            string digest = reader.GetString(0);
            long length = reader.GetInt64(1);
            if (!OriginalFileStore.IsValidDigest(digest) || length < 0)
            {
                throw new InvalidDataException("The snapshot contains invalid original metadata.");
            }

            originals.Add(new SnapshotObject(digest, length));
        }

        return originals;
    }

    internal static SnapshotManifest ReadSnapshotManifest(string backup)
    {
        string path = Path.Combine(backup, "manifest.json");
        long length = new FileInfo(path).Length;
        if (length is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("The snapshot manifest size is invalid.");
        }

        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return JsonSerializer.Deserialize<SnapshotManifest>(input) ?? throw new InvalidDataException("The snapshot manifest is empty.");
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

        string root = CheckPrivateDirectory(Path.GetFullPath(backupDirectory));
        root = CheckPrivateDirectory(FileSystemPaths.ResolveExisting(root));
        HashSet<string> allowed = new(StringComparer.Ordinal)
        {
            "catalog.sqlite", "manifest.json", "objects"
        };
        if (Directory.EnumerateFileSystemEntries(root).Any(entry => !allowed.Contains(Path.GetFileName(entry)) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidDataException("The backup contains an unexpected or linked entry.");
        }

        return sourceArray.Select(file => Path.Combine(root, file)).Any(path => !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            ? throw new InvalidDataException("The backup is incomplete.")
            : root;
    }

    internal static string ValidateNewDestination(string path, string disallowedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The destination must be absolute.", nameof(path));
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.GetDirectoryName(full) ?? throw new ArgumentException("A destination parent is required.", nameof(path));
        CheckPrivateDirectory(parent);
        string resolvedParent = CheckPrivateDirectory(FileSystemPaths.ResolveExisting(parent));
        string destination = Path.Combine(resolvedParent, Path.GetFileName(full));
        if (PathScope.Contains(FileSystemPaths.ResolveExisting(disallowedRoot), destination))
        {
            throw new InvalidOperationException("The destination cannot be inside the source.");
        }

        if (File.Exists(destination) || Directory.Exists(destination) || new DirectoryInfo(destination).LinkTarget is not null)
        {
            throw new IOException("The destination already exists.");
        }

        return destination;
    }

    internal static string CreateStagingDirectory(string destination)
    {
        string parent = Path.GetDirectoryName(destination)!;
        string name = Path.GetFileName(destination);
        string staging = Path.Combine(parent, $".{name}.incomplete-{Guid.NewGuid():N}");
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
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || !Directory.Exists(path))
        {
            throw new InvalidDataException("The directory must exist and cannot be linked.");
        }

        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~PrivateDirectory) != 0)
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
        FileStreamOptions options = new()
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

    internal static async Task CopyCheckedFileAsync(string source, string target, long length, string digest, CancellationToken cancellationToken)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The snapshot contains a linked file.");
        }

        await using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous))
        {
            if (input.Length != length)
            {
                throw new InvalidDataException("The snapshot catalog length has changed.");
            }

            await using FileStream output = OpenNewPrivateFile(target);
            byte[] buffer = new byte[64 * 1024];
            for (long remaining = length; remaining > 0;)
            {
                int count = (int)Math.Min(remaining, buffer.Length);
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

        (_, string copiedHash) = await HashFileAsync(target, cancellationToken, length);
        if (copiedHash != digest)
        {
            throw new InvalidDataException("The copied snapshot catalog failed verification.");
        }
    }

    internal static async Task<(long Length, string Digest)> HashFileAsync(string path, CancellationToken cancellationToken, long? expectedLength = null)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The snapshot contains a linked file.");
        }

        await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = input.Length;
        if (expectedLength is { } expected && length != expected)
        {
            throw new InvalidDataException("The snapshot catalog length has changed.");
        }

        byte[] digest = await SHA256.HashDataAsync(input, cancellationToken);
        return input.Length != length ? throw new InvalidDataException("The snapshot catalog length has changed.") : (length, Convert.ToHexStringLower(digest));
    }

    internal static bool IsSnapshotSchemaVersion(int version) => version is >= 2 and <= CatalogSchema.CurrentVersion;

    internal static void EnsureCatalogFitsBudget(SqliteConnection database, long maxBytes)
    {
        using SqliteCommand count = database.CreateCommand("PRAGMA page_count");
        using SqliteCommand size = database.CreateCommand("PRAGMA page_size");
        long pageCount = Convert.ToInt64(count.ExecuteScalar());
        long pageSize = Convert.ToInt64(size.ExecuteScalar());
        if (pageSize <= 0 || pageCount > maxBytes / pageSize)
        {
            throw new InvalidDataException("The snapshot catalog exceeds the byte budget.");
        }
    }

    internal static long EnforceBudget(long catalogLength, IReadOnlyList<SnapshotObject> objects, long maxBytes)
    {
        if (catalogLength > maxBytes)
        {
            throw new InvalidDataException("The snapshot exceeds its byte budget.");
        }

        long remaining = maxBytes - catalogLength;
        foreach (SnapshotObject? item in objects)
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
    }

    internal static async Task<CatalogSnapshotReport> VerifyContentsAsync(string directory, SnapshotManifest manifest, long maxBytes, CancellationToken cancellationToken)
    {
        if (manifest.FormatVersion != FormatVersion || !IsSnapshotSchemaVersion(manifest.CatalogVersion) || manifest.Objects.Count > MaximumObjects
            || !OriginalFileStore.IsValidDigest(manifest.CatalogSha256) || manifest.CatalogLength < 0)
        {
            throw new InvalidDataException("The snapshot manifest is incompatible.");
        }

        long total = EnforceBudget(manifest.CatalogLength, manifest.Objects, maxBytes);
        string catalogPath = Path.Combine(directory, "catalog.sqlite");
        (_, string hash) = await HashFileAsync(catalogPath, cancellationToken, manifest.CatalogLength);
        if (hash != manifest.CatalogSha256)
        {
            throw new InvalidDataException("The snapshot catalog failed integrity verification.");
        }

        List<SnapshotObject> objects = ReadSnapshotObjects(catalogPath, manifest.CatalogVersion);
        if (!objects.SequenceEqual(manifest.Objects))
        {
            throw new InvalidDataException("The manifest does not match the catalog objects.");
        }

        OriginalFileStore store = new(directory);
        HashSet<string> published = store.EnumeratePublishedDigests().ToHashSet(StringComparer.Ordinal);
        if (published.Count != objects.Count || objects.Any(item => !published.Contains(item.Digest)))
        {
            throw new InvalidDataException("The snapshot object inventory is incomplete or unexpected.");
        }

        foreach (SnapshotObject item in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.VerifyAsync(new OriginalObject(item.Digest, item.Length), cancellationToken);
        }

        return new CatalogSnapshotReport(objects.Count, total, hash);
    }
}
