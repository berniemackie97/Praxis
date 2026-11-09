using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

public sealed record OriginalRecoveryInspection(
    int StagedFiles, int PublishedObjects, int ReferencedObjects,
    int UnreferencedObjects, int MissingObjects, int CorruptObjects);

/// <summary>
/// Recovery operations on an open catalog: original-store inspection and coherent live backup.
/// </summary>
public sealed class LocalCatalogRecovery
{
    private readonly CatalogDatabase database;
    private readonly OriginalFileStore objects;
    private readonly string dataDirectory;

    internal LocalCatalogRecovery(CatalogDatabase database, OriginalFileStore objects, string dataDirectory)
    {
        this.database = database;
        this.objects = objects;
        this.dataDirectory = dataDirectory;
    }

    public Task<LocalSnapshotReport> BackupAsync(
        string destinationDirectory, long maxBytes,
        CancellationToken cancellationToken = default) =>
        BackupAsync(destinationDirectory, maxBytes, CatalogSchema.CurrentVersion, cancellationToken);

    internal int ReadStoredSchemaVersion() => database.Execute(CatalogSchema.ReadVersion);

    internal async Task<LocalSnapshotReport> BackupAsync(
        string destinationDirectory, long maxBytes, int catalogVersion,
        CancellationToken cancellationToken)
    {
        CatalogDirectory.Validate(dataDirectory);
        var destination = SnapshotFiles.ValidateNewDestination(destinationDirectory, dataDirectory);
        SnapshotFiles.ValidateBudget(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var staging = SnapshotFiles.CreateStagingDirectory(destination);
        var catalogPath = Path.Combine(staging, "catalog.sqlite");
        using (var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalogPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            snapshot.Open();
            database.Execute(connection =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                SnapshotFiles.EnsureCatalogFitsBudget(connection, maxBytes);
                connection.BackupDatabase(snapshot);
            });

            using var journal = snapshot.CreateCommand("PRAGMA journal_mode = DELETE");
            if (!string.Equals(journal.ExecuteScalar() as string,
                "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The snapshot could not be finalized.");
            }
        }

        SnapshotFiles.RestrictFile(catalogPath);
        var snapshotObjects = SnapshotFiles.ReadSnapshotObjects(catalogPath, catalogVersion);
        var (catalogLength, catalogDigest) =
            await SnapshotFiles.HashFileAsync(catalogPath, cancellationToken);
        SnapshotFiles.EnforceBudget(catalogLength, snapshotObjects, maxBytes);
        var target = new OriginalFileStore(staging);
        foreach (var original in snapshotObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await objects.CopyVerifiedToStoreAsync(
                new OriginalObject(original.Digest, original.Length),
                target, cancellationToken);
        }

        var manifest = new LocalSnapshotManifest(
            SnapshotFiles.FormatVersion, catalogVersion,
            catalogDigest, catalogLength, snapshotObjects);
        var manifestPath = Path.Combine(staging, "manifest.json");
        await using (var output = SnapshotFiles.OpenNewPrivateFile(manifestPath))
        {
            await JsonSerializer.SerializeAsync(output, manifest,
                cancellationToken: cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        var verified = await LocalCatalogSnapshots.VerifyAsync(staging, maxBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(staging, destination);
        return verified;
    }

    public async Task<OriginalRecoveryInspection> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        var expected = new Dictionary<string, OriginalObject>(StringComparer.Ordinal);
        database.Execute(connection =>
        {
            using var query = connection.CreateCommand("""
                SELECT DISTINCT o.digest, o.byte_length
                FROM item_versions v
                JOIN original_objects o ON o.digest = v.digest
                """);
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                var original = new OriginalObject(reader.GetString(0), reader.GetInt64(1));
                expected.Add(original.Digest, original);
            }
        });

        var referenced = expected.Count;
        var staged = objects.CountStagedFiles();
        var published = 0;
        var unreferenced = 0;
        var missing = 0;
        var corrupt = 0;

        foreach (var digest in objects.EnumeratePublishedDigests())
        {
            cancellationToken.ThrowIfCancellationRequested();
            published++;
            if (!expected.Remove(digest, out var original))
            {
                unreferenced++;
                continue;
            }

            try
            {
                await objects.VerifyAsync(original, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                missing++;
            }
            catch (DirectoryNotFoundException)
            {
                missing++;
            }
            catch (InvalidDataException)
            {
                corrupt++;
            }
        }

        return new OriginalRecoveryInspection(staged, published, referenced,
            unreferenced, missing + expected.Count, corrupt);
    }
}
