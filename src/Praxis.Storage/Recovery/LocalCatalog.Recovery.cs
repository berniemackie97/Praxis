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
        var expected = new Dictionary<string, OriginalObject>(StringComparer.Ordinal);
        lock (connection)
        {
            using var query = Command(connection, """
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
        }

        var referenced = expected.Count;
        var store = new OriginalFileStore(Path.GetDirectoryName(DatabasePath)!);
        var staged = store.CountStagedFiles();
        var published = 0;
        var unreferenced = 0;
        var missing = 0;
        var corrupt = 0;

        foreach (var digest in store.EnumeratePublishedDigests())
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
