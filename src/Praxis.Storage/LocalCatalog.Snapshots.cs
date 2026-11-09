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
        string root = Path.GetDirectoryName(DatabasePath)!;
        ValidateRoot(root);
        string destination = ValidateNewDestination(destinationDirectory, root);
        ValidateBudget(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();

        string staging = CreateStagingDirectory(destination);
        string catalogPath = Path.Combine(staging, "catalog.sqlite");
        using (SqliteConnection snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
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

            using SqliteCommand journal = Command(snapshot, "PRAGMA journal_mode = DELETE");
            if (!string.Equals(journal.ExecuteScalar() as string,
                "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The snapshot could not be finalized.");
            }
        }

        RestrictFile(catalogPath);
        List<LocalSnapshotObject> objects = ReadSnapshotObjects(catalogPath, CurrentSchemaVersion);
        (long catalogLength, string catalogDigest) =
            await HashFileAsync(catalogPath, cancellationToken);
        long totalBytes = EnforceBudget(catalogLength, objects, maxBytes);
        OriginalFileStore source = new OriginalFileStore(root);
        OriginalFileStore target = new OriginalFileStore(staging);
        foreach (LocalSnapshotObject original in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await source.CopyVerifiedAsync(
                new OriginalObject(original.Digest, original.Length),
                target, cancellationToken);
        }

        LocalSnapshotManifest manifest = new LocalSnapshotManifest(
            SnapshotFormatVersion, CurrentSchemaVersion,
            catalogDigest, catalogLength, objects);
        string manifestPath = Path.Combine(staging, "manifest.json");
        await using (FileStream output = OpenNewPrivateFile(manifestPath))
        {
            await JsonSerializer.SerializeAsync(output, manifest,
                cancellationToken: cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        LocalSnapshotReport verified = await VerifyBackupAsync(staging, maxBytes, cancellationToken);
        Directory.Move(staging, destination);
        return verified with { TotalBytes = totalBytes };
    }

    public static async Task<LocalSnapshotReport> RestoreAsync(
        string backupDirectory, string destinationDirectory, long maxBytes,
        CancellationToken cancellationToken = default)
    {
        string backup = ValidateExistingBackup(backupDirectory);
        string destination = ValidateNewDestination(destinationDirectory, backup);
        ValidateBudget(maxBytes);
        LocalSnapshotReport verified = await VerifyBackupAsync(backup, maxBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        string staging = CreateStagingDirectory(destination);
        LocalSnapshotManifest expected = ReadSnapshotManifest(backup);
        await CopyCheckedFileAsync(
            Path.Combine(backup, "catalog.sqlite"),
            Path.Combine(staging, "catalog.sqlite"),
            expected.CatalogLength, expected.CatalogSha256, cancellationToken);

        OriginalFileStore source = new OriginalFileStore(backup);
        OriginalFileStore target = new OriginalFileStore(staging);
        foreach (LocalSnapshotObject original in expected.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await source.CopyVerifiedAsync(
                new OriginalObject(original.Digest, original.Length),
                target, cancellationToken);
        }

        await VerifySnapshotContentsAsync(staging, expected, maxBytes, cancellationToken);
        using (LocalCatalog restored = Open(staging))
        {
            OriginalRecoveryInspection inspection = await restored.InspectRecoveryAsync(cancellationToken);
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
        string backup = ValidateExistingBackup(backupDirectory);
        ValidateBudget(maxBytes);
        LocalSnapshotManifest manifest = ReadSnapshotManifest(backup);
        return await VerifySnapshotContentsAsync(
            backup, manifest, maxBytes, cancellationToken);
    }

    private static async Task<LocalSnapshotReport> VerifySnapshotContentsAsync(
        string directory, LocalSnapshotManifest manifest, long maxBytes,
        CancellationToken cancellationToken)
    {
        if (manifest.FormatVersion != SnapshotFormatVersion ||
            manifest.CatalogVersion is not (2 or 3 or 4 or CurrentSchemaVersion) ||
            manifest.Objects is null ||
            manifest.Objects.Count > MaximumSnapshotObjects ||
            !OriginalFileStore.IsValidDigest(manifest.CatalogSha256) || manifest.CatalogLength < 0)
        {
            throw new InvalidDataException("The snapshot manifest is incompatible.");
        }

        (long length, string hash) = await HashFileAsync(
            Path.Combine(directory, "catalog.sqlite"), cancellationToken);
        if (length != manifest.CatalogLength || hash != manifest.CatalogSha256)
        {
            throw new InvalidDataException("The snapshot catalog failed integrity verification.");
        }

        List<LocalSnapshotObject> objects = ReadSnapshotObjects(Path.Combine(directory, "catalog.sqlite"), manifest.CatalogVersion);
        if (!objects.SequenceEqual(manifest.Objects))
        {
            throw new InvalidDataException("The manifest does not match the catalog objects.");
        }

        long total = EnforceBudget(manifest.CatalogLength, objects, maxBytes);

        OriginalFileStore store = new OriginalFileStore(directory);
        HashSet<string> published = store.EnumeratePublishedDigests().ToHashSet(StringComparer.Ordinal);
        if (published.Count != objects.Count ||
            objects.Any(item => !published.Contains(item.Digest)))
        {
            throw new InvalidDataException("The snapshot object inventory is incomplete or unexpected.");
        }

        foreach (LocalSnapshotObject item in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.VerifyAsync(
                new OriginalObject(item.Digest, item.Length), cancellationToken);
        }

        return new LocalSnapshotReport(objects.Count, total, hash);
    }
}
