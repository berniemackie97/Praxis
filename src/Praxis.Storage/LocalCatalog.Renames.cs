using System.Globalization;

namespace Praxis.Storage;

public sealed partial class LocalCatalog
{
    internal bool RelocateObservedFile(
        string previousPath, string currentPath, string verifiedDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedDigest);
        if (!Path.IsPathFullyQualified(previousPath) || !Path.IsPathFullyQualified(currentPath) ||
            verifiedDigest.Length != 64 || verifiedDigest.Any(c =>
                c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("The relocation has invalid path or digest evidence.");
        }

        var old = Path.GetFullPath(previousPath);
        var current = Path.GetFullPath(currentPath);
        if (old == current)
        {
            return false;
        }

        lock (connection)
        {
            using var transaction = connection.BeginTransaction();
            using var eligible = Command(connection, """
                SELECT s.source_id
                FROM sources s JOIN items i ON i.source_id = s.source_id
                JOIN item_versions v ON v.item_id = i.item_id
                WHERE s.source_kind = 'file' AND s.status = 'active'
                  AND s.locator = $old AND v.digest = $digest
                  AND v.revision = (SELECT MAX(latest.revision)
                      FROM item_versions latest WHERE latest.item_id = i.item_id)
                  AND NOT EXISTS (SELECT 1 FROM sources occupied
                      WHERE occupied.source_kind = 'file' AND occupied.locator = $new)
                """);
            eligible.Transaction = transaction;
            eligible.Parameters.AddWithValue("$old", old);
            eligible.Parameters.AddWithValue("$new", current);
            eligible.Parameters.AddWithValue("$digest", verifiedDigest);
            if (eligible.ExecuteScalar() is not string sourceId)
            {
                return false;
            }

            using var move = Command(connection, """
                UPDATE sources SET locator = $new
                WHERE source_id = $source AND locator = $old AND status = 'active'
                """);
            move.Transaction = transaction;
            move.Parameters.AddWithValue("$new", current);
            move.Parameters.AddWithValue("$source", sourceId);
            move.Parameters.AddWithValue("$old", old);
            if (move.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException("The observed source changed during relocation.");
            }

            using var history = Command(connection, """
                INSERT INTO source_locator_moves
                    (move_id, source_id, old_locator, new_locator, digest, recorded_utc)
                VALUES ($id, $source, $old, $new, $digest, $at)
                """);
            history.Transaction = transaction;
            history.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            history.Parameters.AddWithValue("$source", sourceId);
            history.Parameters.AddWithValue("$old", old);
            history.Parameters.AddWithValue("$new", current);
            history.Parameters.AddWithValue("$digest", verifiedDigest);
            history.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            history.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }
    }
}
