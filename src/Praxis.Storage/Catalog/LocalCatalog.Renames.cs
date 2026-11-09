using System.Globalization;

namespace Praxis.Storage;

public sealed record LocalSourceMovement(
    Guid SourceId, string PreviousPath, string CurrentPath, string Sha256, DateTimeOffset RecordedUtc);

public sealed partial class LocalCatalog
{
    public IReadOnlyList<LocalSourceMovement> ListSourceMovements(Guid sourceId, int limit = 20)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A source identifier is required.", nameof(sourceId));
        }

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        lock (connection)
        {
            using var query = connection.CreateCommand("""
                SELECT m.source_id, m.old_locator, m.new_locator, m.digest, m.recorded_utc
                FROM source_locator_moves m
                JOIN sources s ON s.source_id = m.source_id
                WHERE s.status = 'active' AND s.source_id = $source
                ORDER BY m.recorded_utc DESC, m.move_id DESC LIMIT $limit
                """);
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            query.Parameters.AddWithValue("$limit", limit);
            using var reader = query.ExecuteReader();
            var moves = new List<LocalSourceMovement>();
            while (reader.Read())
            {
                moves.Add(new LocalSourceMovement(
                    Guid.Parse(reader.GetString(0)), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
            }

            return moves;
        }
    }

    internal long CountMovesFrom(string previousPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousPath);
        lock (connection)
        {
            using var command = connection.CreateCommand(
                "SELECT COUNT(*) FROM source_locator_moves WHERE old_locator = $path");
            command.Parameters.AddWithValue("$path", Path.GetFullPath(previousPath));
            return (long)command.ExecuteScalar()!;
        }
    }

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
            using var eligible = connection.CreateCommand("""
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

            using var move = connection.CreateCommand("""
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

            using var history = connection.CreateCommand("""
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
