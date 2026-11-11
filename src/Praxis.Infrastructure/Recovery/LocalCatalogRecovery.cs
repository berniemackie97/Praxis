using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Catalog;
using Praxis.Infrastructure.Originals;

namespace Praxis.Infrastructure.Recovery;

public sealed record OriginalRecoveryInspection(int StagedFiles, int PublishedObjects, int ReferencedObjects, int UnreferencedObjects, int MissingObjects, int CorruptObjects);

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

    public Task<LocalSnapshotReport> BackupAsync(string destinationDirectory, long maxBytes, CancellationToken cancellationToken = default) => BackupAsync(destinationDirectory, maxBytes, CatalogSchema.CurrentVersion, cancellationToken);

    internal int ReadStoredSchemaVersion() => database.Execute(CatalogSchema.ReadVersion);

    internal async Task<LocalSnapshotReport> BackupAsync(string destinationDirectory, long maxBytes, int catalogVersion, CancellationToken cancellationToken)
    {
        CatalogDirectory.Validate(dataDirectory);
        string destination = SnapshotFiles.ValidateNewDestination(destinationDirectory, dataDirectory);
        SnapshotFiles.ValidateBudget(maxBytes);
        cancellationToken.ThrowIfCancellationRequested();

        string staging = SnapshotFiles.CreateStagingDirectory(destination);
        string catalogPath = Path.Combine(staging, "catalog.sqlite");
        using (SqliteConnection snapshot = new(new SqliteConnectionStringBuilder
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

            using SqliteCommand journal = snapshot.CreateCommand("PRAGMA journal_mode = DELETE");
            if (!string.Equals(journal.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The snapshot could not be finalized.");
            }
        }

        SnapshotFiles.RestrictFile(catalogPath);
        List<LocalSnapshotObject> snapshotObjects = SnapshotFiles.ReadSnapshotObjects(catalogPath, catalogVersion);
        (long catalogLength, string catalogDigest) = await SnapshotFiles.HashFileAsync(catalogPath, cancellationToken);
        SnapshotFiles.EnforceBudget(catalogLength, snapshotObjects, maxBytes);
        OriginalFileStore target = new(staging);
        foreach (LocalSnapshotObject original in snapshotObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await objects.CopyVerifiedToStoreAsync(new OriginalObject(original.Digest, original.Length), target, cancellationToken);
        }

        LocalSnapshotManifest manifest = new(SnapshotFiles.FormatVersion, catalogVersion, catalogDigest, catalogLength, snapshotObjects);
        string manifestPath = Path.Combine(staging, "manifest.json");
        await using (FileStream output = SnapshotFiles.OpenNewPrivateFile(manifestPath))
        {
            await JsonSerializer.SerializeAsync(output, manifest, cancellationToken: cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
        }

        LocalSnapshotReport verified = await LocalCatalogSnapshots.VerifyAsync(staging, maxBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.Move(staging, destination);
        return verified;
    }

    public async Task<OriginalRecoveryInspection> InspectAsync(CancellationToken cancellationToken = default)
    {
        Dictionary<string, OriginalObject> expected = new(StringComparer.Ordinal);
        database.Execute(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT DISTINCT o.digest, o.byte_length
                                                                 FROM item_versions v
                                                                 JOIN original_objects o ON o.digest = v.digest
                                                                 """);
            using SqliteDataReader reader = query.ExecuteReader();
            while (reader.Read())
            {
                OriginalObject original = new(reader.GetString(0), reader.GetInt64(1));
                expected.Add(original.Digest, original);
            }
        });

        int referenced = expected.Count;
        int staged = objects.CountStagedFiles();
        int published = 0;
        int unreferenced = 0;
        int missing = 0;
        int corrupt = 0;

        foreach (string digest in objects.EnumeratePublishedDigests())
        {
            cancellationToken.ThrowIfCancellationRequested();
            published++;
            if (!expected.Remove(digest, out OriginalObject original))
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

        return new OriginalRecoveryInspection(staged, published, referenced, unreferenced, missing + expected.Count, corrupt);
    }
}
