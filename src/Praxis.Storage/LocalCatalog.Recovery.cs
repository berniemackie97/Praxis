using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

public sealed record OriginalRecoveryInspection(
    int StagedFiles, int PublishedObjects, int ReferencedObjects,
    int UnreferencedObjects, int MissingObjects, int CorruptObjects);

public sealed partial class LocalCatalog
{
    public async Task<OriginalRecoveryInspection> InspectRecoveryAsync(
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, OriginalObject> expected = new Dictionary<string, OriginalObject>(StringComparer.Ordinal);
        lock (connection)
        {
            using SqliteCommand query = Command(connection, """
                                                            SELECT DISTINCT o.digest, o.byte_length
                                                            FROM item_versions v
                                                            JOIN original_objects o ON o.digest = v.digest
                                                            """);
            using SqliteDataReader reader = query.ExecuteReader();
            while (reader.Read())
            {
                OriginalObject original = new OriginalObject(reader.GetString(0), reader.GetInt64(1));
                expected.Add(original.Digest, original);
            }
        }

        int referenced = expected.Count;
        OriginalFileStore store = new OriginalFileStore(Path.GetDirectoryName(DatabasePath)!);
        int staged = store.CountStagedFiles();
        int published = 0;
        int unreferenced = 0;
        int missing = 0;
        int corrupt = 0;

        foreach (string digest in store.EnumeratePublishedDigests())
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
                await store.VerifyAsync(original, cancellationToken);
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
