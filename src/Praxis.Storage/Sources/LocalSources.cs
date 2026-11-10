using Microsoft.Data.Sqlite;
using Praxis.Storage.Catalog;

namespace Praxis.Storage.Sources;

public sealed record LocalFileVersion(Guid SourceId, Guid ItemId, int Revision, string Sha256, string Status);

internal sealed record CurrentFileVersion(Guid ItemId, int Revision, string Digest, long Length, string Locator);

public sealed record LocalSourceMovement(Guid SourceId, string PreviousPath, string CurrentPath, string Sha256, DateTimeOffset RecordedUtc);

/// <summary>
/// Identity and lifecycle of captured sources: path lookup, observed relocations and revocation.
/// </summary>
public sealed class LocalSources
{
    private readonly CatalogDatabase database;

    internal LocalSources(CatalogDatabase database) => this.database = database;

    public LocalFileVersion? FindLatestFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The source path must be absolute.", nameof(path));
        }

        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                                                                   SELECT s.source_id, i.item_id, v.revision, v.digest, s.status
                                                                   FROM sources s
                                                                   JOIN items i ON i.source_id = s.source_id
                                                                   JOIN item_versions v ON v.item_id = i.item_id
                                                                   WHERE s.source_kind = 'file' AND s.locator = $path
                                                                   ORDER BY v.revision DESC LIMIT 1
                                                                   """);
            command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
            using SqliteDataReader reader = command.ExecuteReader();
            return !reader.Read() ? null : new LocalFileVersion(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2), reader.GetString(3), reader.GetString(4));
        });
    }

    /// <summary>Latest revision of an active file source, or null when unavailable.</summary>
    internal CurrentFileVersion? CurrentFile(Guid sourceId) => database.Execute(connection =>
    {
        using SqliteCommand query = connection.CreateCommand("""
                                                             SELECT i.item_id, v.revision, v.digest, o.byte_length, s.locator
                                                             FROM sources s
                                                             JOIN items i ON i.source_id = s.source_id
                                                             JOIN item_versions v ON v.item_id = i.item_id
                                                             JOIN original_objects o ON o.digest = v.digest
                                                             WHERE s.source_id = $source AND s.status = 'active' AND s.source_kind = 'file'
                                                               AND v.revision = (SELECT MAX(latest.revision)
                                                                                 FROM item_versions latest WHERE latest.item_id = v.item_id)
                                                             LIMIT 1
                                                             """);
        query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
        using SqliteDataReader reader = query.ExecuteReader();
        return reader.Read()
            ? new CurrentFileVersion(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), reader.GetString(4))
            : null;
    });

    /// <summary>
    /// Revokes an active source and cancels its unfinished processing in one transaction.
    /// Originals and history stay immutable; derived reads filter on source status.
    /// </summary>
    public bool Revoke(Guid sourceId)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A source ID is required.", nameof(sourceId));
        }

        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            bool revoked = RevokeWithin(connection, transaction, sourceId.ToString("D"), CatalogValues.Timestamp(DateTimeOffset.UtcNow));
            transaction.Commit();
            return revoked;
        });
    }

    /// <summary>Active file sources with their stored locator and current digest.</summary>
    internal IReadOnlyList<(Guid SourceId, string Locator, string Digest)> ActiveFileLocators() => database.Execute(connection =>
    {
        using SqliteCommand query = connection.CreateCommand("""
                                                             SELECT s.source_id, s.locator, v.digest
                                                             FROM sources s
                                                             JOIN items i ON i.source_id = s.source_id
                                                             JOIN item_versions v ON v.item_id = i.item_id
                                                             WHERE s.source_kind = 'file' AND s.status = 'active'
                                                               AND v.revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = i.item_id)
                                                             ORDER BY s.created_utc, s.source_id
                                                             """);
        using SqliteDataReader reader = query.ExecuteReader();
        List<(Guid, string, string)> sources = new();
        while (reader.Read())
        {
            sources.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)));
        }

        return sources;
    });

    /// <summary>
    /// Folds an active source recorded under a non-canonical spelling into the source that
    /// already uses the canonical spelling: collection memberships and proposal reviews carry
    /// over to matching proposals, then the alias is revoked. When the canonical source was
    /// revoked by the owner, the alias is revoked too. Returns false when nothing changed.
    /// </summary>
    internal bool FoldAlias(Guid aliasSourceId, string canonicalLocator) => database.Execute(connection =>
    {
        string now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
        string alias = aliasSourceId.ToString("D");
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand target = connection.CreateCommand("SELECT source_id, status FROM sources WHERE source_kind = 'file' AND locator = $locator");
        target.Transaction = transaction;
        target.Parameters.AddWithValue("$locator", canonicalLocator);
        string? canonical = null;
        string? status = null;
        using (SqliteDataReader reader = target.ExecuteReader())
        {
            if (reader.Read())
            {
                canonical = reader.GetString(0);
                status = reader.GetString(1);
            }
        }

        if (canonical is null || canonical == alias)
        {
            return false;
        }

        if (status == "active")
        {
            connection.Run(transaction, """
                                        INSERT OR IGNORE INTO source_collection_memberships (collection_id, source_id, created_utc)
                                        SELECT collection_id, $canonical, created_utc FROM source_collection_memberships WHERE source_id = $alias
                                        """, ("$canonical", canonical), ("$alias", alias));
            ReviewCarryOver.Apply(connection, transaction, alias, canonical, now);
        }

        bool revoked = RevokeWithin(connection, transaction, alias, now);
        transaction.Commit();
        return revoked;
    });

    private static bool RevokeWithin(SqliteConnection connection, SqliteTransaction transaction, string sourceId, string now)
    {
        if (connection.Run(transaction, "UPDATE sources SET status = 'revoked' WHERE source_id = $source AND status = 'active'", ("$source", sourceId)) == 0)
        {
            return false;
        }

        connection.Run(transaction, """
                                    UPDATE processing_jobs
                                    SET state = 'canceled', failure_code = NULL, lease_key = NULL, lease_until_utc = NULL, updated_utc = $now
                                    WHERE item_id IN (SELECT item_id FROM items WHERE source_id = $source)
                                      AND state IN ('queued', 'running', 'failed')
                                    """, ("$source", sourceId), ("$now", now));
        return true;
    }

    public IReadOnlyList<LocalSourceMovement> ListMovements(Guid sourceId, int limit = 20)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A source identifier is required.", nameof(sourceId));
        }

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return database.Execute(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT m.source_id, m.old_locator, m.new_locator, m.digest, m.recorded_utc
                                                                 FROM source_locator_moves m
                                                                 JOIN sources s ON s.source_id = m.source_id
                                                                 WHERE s.status = 'active' AND s.source_id = $source
                                                                 ORDER BY m.recorded_utc DESC, m.move_id DESC LIMIT $limit
                                                                 """);
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            query.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = query.ExecuteReader();
            List<LocalSourceMovement> moves = new();
            while (reader.Read())
            {
                moves.Add(new LocalSourceMovement(
                    Guid.Parse(reader.GetString(0)), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3),
                    CatalogValues.ParseTimestamp(reader.GetString(4))));
            }

            return moves;
        });
    }

    internal long CountMovesFrom(string previousPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousPath);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("SELECT COUNT(*) FROM source_locator_moves WHERE old_locator = $path");
            command.Parameters.AddWithValue("$path", Path.GetFullPath(previousPath));
            return (long)command.ExecuteScalar()!;
        });
    }

    internal bool RelocateObservedFile(string previousPath, string currentPath, string verifiedDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedDigest);
        if (!Path.IsPathFullyQualified(previousPath) || !Path.IsPathFullyQualified(currentPath) || verifiedDigest.Length != 64 || verifiedDigest.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("The relocation has invalid path or digest evidence.");
        }

        string old = Path.GetFullPath(previousPath);
        string current = Path.GetFullPath(currentPath);
        if (old == current)
        {
            return false;
        }

        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand eligible = connection.CreateCommand("""
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

            using SqliteCommand move = connection.CreateCommand("""
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

            using SqliteCommand history = connection.CreateCommand("""
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
            history.Parameters.AddWithValue("$at", CatalogValues.Timestamp(DateTimeOffset.UtcNow));
            history.ExecuteNonQuery();
            transaction.Commit();
            return true;
        });
    }
}
