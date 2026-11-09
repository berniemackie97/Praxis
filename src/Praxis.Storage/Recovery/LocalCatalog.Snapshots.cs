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

            using var journal = snapshot.CreateCommand("PRAGMA journal_mode = DELETE");
            if (!string.Equals(journal.ExecuteScalar() as string,
                "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The snapshot could not be finalized.");
            }
        }

        RestrictFile(catalogPath);
        var objects = ReadSnapshotObjects(catalogPath, CatalogSchema.CurrentVersion);
        var (catalogLength, catalogDigest) =
            await HashFileAsync(catalogPath, cancellationToken);
        var totalBytes = EnforceBudget(catalogLength, objects, maxBytes);
        var source = new OriginalFileStore(root);
        var target = new OriginalFileStore(staging);
        foreach (var original in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await source.CopyVerifiedToStoreAsync(
                new OriginalObject(original.Digest, original.Length),
                target, cancellationToken);
        }

        var manifest = new LocalSnapshotManifest(
            SnapshotFormatVersion, CatalogSchema.CurrentVersion,
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
            await source.CopyVerifiedToStoreAsync(
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
            manifest.CatalogVersion is < 2 or > CatalogSchema.CurrentVersion ||
            manifest.Objects is null ||
            manifest.Objects.Count > MaximumSnapshotObjects ||
            !OriginalFileStore.IsValidDigest(manifest.CatalogSha256) || manifest.CatalogLength < 0)
        {
            throw new InvalidDataException("The snapshot manifest is incompatible.");
        }

        var (length, hash) = await HashFileAsync(
            Path.Combine(directory, "catalog.sqlite"), cancellationToken);
        if (length != manifest.CatalogLength || hash != manifest.CatalogSha256)
        {
            throw new InvalidDataException("The snapshot catalog failed integrity verification.");
        }

        var objects = ReadSnapshotObjects(Path.Combine(directory, "catalog.sqlite"), manifest.CatalogVersion);
        if (!objects.SequenceEqual(manifest.Objects))
        {
            throw new InvalidDataException("The manifest does not match the catalog objects.");
        }

        var total = EnforceBudget(manifest.CatalogLength, objects, maxBytes);

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
