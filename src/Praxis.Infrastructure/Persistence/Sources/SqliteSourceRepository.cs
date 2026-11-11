using Microsoft.Data.Sqlite;
using Praxis.Application.Sources;
using Praxis.Domain.Processing;
using Praxis.Domain.Sources;

namespace Praxis.Infrastructure.Persistence.Sources;

/// <summary>
/// Identity and lifecycle of captured sources: path lookup, observed relocations and revocation.
/// </summary>
public sealed class SqliteSourceRepository : ISourceRepository
{
    private readonly CatalogDatabase database;

    private readonly TimeProvider clock;

    internal SqliteSourceRepository(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    /// <summary>
    /// Canonical, source-scoped provenance for one recorded revision. Revoked and older
    /// revisions remain inspectable to the trusted local owner, not searchable evidence.
    /// </summary>
    public SourceProvenance? ReadProvenance(Guid sourceId, int? revision = null)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A source identifier is required.", nameof(sourceId));
        }

        if (revision is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(revision));
        }

        return database.ExecuteRead(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT s.source_id, i.item_id, s.source_kind, s.locator, s.status,
                                                                        s.created_utc, v.revision, v.observed_utc, v.digest,
                                                                        o.byte_length, j.processor_key, j.state, j.failure_code,
                                                                        d.outcome, d.reason_code,
                                                                        c.route, c.authority_basis, c.scope, c.recorded_utc,
                                                                        CASE WHEN s.status = 'active'
                                                                          AND j.state IN ('failed', 'canceled')
                                                                          AND v.revision = (
                                                                              SELECT MAX(latest.revision)
                                                                              FROM item_versions latest WHERE latest.item_id = i.item_id)
                                                                        THEN 1 ELSE 0 END
                                                                 FROM sources s
                                                                 JOIN items i ON i.source_id = s.source_id
                                                                 JOIN item_versions v ON v.item_id = i.item_id
                                                                 JOIN original_objects o ON o.digest = v.digest
                                                                 LEFT JOIN processing_jobs j ON j.item_id = v.item_id
                                                                     AND j.revision = v.revision AND j.processor_key = $processor
                                                                 LEFT JOIN text_derivatives d ON d.item_id = v.item_id
                                                                     AND d.revision = v.revision AND d.processor_key = j.processor_key
                                                                     AND d.source_digest = v.digest
                                                                 LEFT JOIN ingest_requests r ON r.request_key = (
                                                                     SELECT first_receipt.request_key
                                                                     FROM ingest_requests first_receipt
                                                                     WHERE first_receipt.source_id = s.source_id
                                                                       AND first_receipt.revision = v.revision
                                                                       AND first_receipt.state = 'committed'
                                                                     ORDER BY first_receipt.rowid LIMIT 1)
                                                                 LEFT JOIN capture_request_context c ON c.request_key = r.request_key
                                                                 WHERE s.source_id = $source
                                                                   AND ($revision IS NULL OR v.revision = $revision)
                                                                 ORDER BY v.revision DESC LIMIT 1
                                                                 """);
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            query.Parameters.AddWithValue("$revision", revision.HasValue ? (object)revision.Value : DBNull.Value);
            query.Parameters.AddWithValue("$processor", ProcessorKeys.SourceText);
            using SqliteDataReader reader = query.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new SourceProvenance(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                CatalogValues.ParseTimestamp(reader.GetString(5)), reader.GetInt32(6),
                CatalogValues.ParseTimestamp(reader.GetString(7)), reader.GetString(8),
                reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : CatalogValues.ParseTimestamp(reader.GetString(18)),
                reader.GetInt64(19) == 1);
        });
    }

    /// <summary>All committed receipt routes for one source, including legacy unattributed entries.</summary>
    public IReadOnlyList<CaptureReceipt> ListCaptureReceipts(Guid sourceId, int? revision = null, int limit = 100)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A source identifier is required.", nameof(sourceId));
        }

        if (revision is < 1 || limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(revision is < 1 ? nameof(revision) : nameof(limit));
        }

        return database.ExecuteRead(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT r.request_key, r.revision, c.route,
                                                                        c.authority_basis, c.scope, c.recorded_utc
                                                                 FROM ingest_requests r
                                                                 LEFT JOIN capture_request_context c ON c.request_key = r.request_key
                                                                 WHERE r.source_id = $source AND r.state = 'committed'
                                                                   AND r.revision IS NOT NULL
                                                                   AND ($revision IS NULL OR r.revision = $revision)
                                                                 ORDER BY r.rowid DESC LIMIT $limit
                                                                 """);
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            query.Parameters.AddWithValue("$revision", revision.HasValue ? (object)revision.Value : DBNull.Value);
            query.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = query.ExecuteReader();
            List<CaptureReceipt> result = new();
            while (reader.Read())
            {
                result.Add(new CaptureReceipt(reader.GetString(0), reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : CatalogValues.ParseTimestamp(reader.GetString(5))));
            }

            return result;
        });
    }

    /// <summary>Versioned processing status for all registered processors of one source.</summary>
    public IReadOnlyList<SourceProcessingState> ListProcessing(Guid sourceId, int? revision = null, int limit = 100)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A source identifier is required.", nameof(sourceId));
        }

        if (revision is < 1 || limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(revision is < 1 ? nameof(revision) : nameof(limit));
        }

        return database.ExecuteRead(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT j.processor_key, j.state, j.attempt, j.total_attempts,
                                                                        j.failure_code, j.updated_utc,
                                                                        CASE WHEN s.status = 'active'
                                                                          AND j.state IN ('failed', 'canceled')
                                                                          AND j.revision = (
                                                                              SELECT MAX(v.revision) FROM item_versions v
                                                                              WHERE v.item_id = j.item_id)
                                                                        THEN 1 ELSE 0 END
                                                                 FROM processing_jobs j
                                                                 JOIN items i ON i.item_id = j.item_id
                                                                 JOIN sources s ON s.source_id = i.source_id
                                                                 WHERE s.source_id = $source
                                                                   AND j.revision = COALESCE($revision, (
                                                                       SELECT MAX(v.revision) FROM item_versions v
                                                                       WHERE v.item_id = j.item_id))
                                                                 ORDER BY j.updated_utc DESC, j.processor_key LIMIT $limit
                                                                 """);
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            query.Parameters.AddWithValue("$revision", revision.HasValue ? (object)revision.Value : DBNull.Value);
            query.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = query.ExecuteReader();
            List<SourceProcessingState> result = new();
            while (reader.Read())
            {
                result.Add(new SourceProcessingState(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                    reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    CatalogValues.ParseTimestamp(reader.GetString(5)), reader.GetInt64(6) == 1));
            }

            return result;
        });
    }

    public SourceFileVersion? FindLatestFile(string path)
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
            return !reader.Read() ? null : new SourceFileVersion(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2), reader.GetString(3), reader.GetString(4));
        });
    }

    /// <summary>Latest revision of an active file source, or null when unavailable.</summary>
    public CurrentFileVersion? CurrentFile(Guid sourceId) => database.Execute(connection =>
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
            bool revoked = RevokeWithin(connection, transaction, sourceId.ToString("D"), CatalogValues.Timestamp(clock.GetUtcNow()));
            transaction.Commit();
            return revoked;
        });
    }

    /// <summary>Active file sources with their stored locator and current digest.</summary>
    public IReadOnlyList<(Guid SourceId, string Locator, string Digest)> ActiveFileLocators() => database.Execute(connection =>
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
    public bool FoldAlias(Guid aliasSourceId, string canonicalLocator) => database.Execute(connection =>
    {
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
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

    public IReadOnlyList<SourceMovement> ListMovements(Guid sourceId, int limit = 20)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A source identifier is required.", nameof(sourceId));
        }

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return database.ExecuteRead(connection =>
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
            List<SourceMovement> moves = new();
            while (reader.Read())
            {
                moves.Add(new SourceMovement(
                    Guid.Parse(reader.GetString(0)), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3),
                    CatalogValues.ParseTimestamp(reader.GetString(4))));
            }

            return moves;
        });
    }

    public long CountMovesFrom(string previousPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousPath);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("SELECT COUNT(*) FROM source_locator_moves WHERE old_locator = $path");
            command.Parameters.AddWithValue("$path", Path.GetFullPath(previousPath));
            return (long)command.ExecuteScalar()!;
        });
    }

    public bool RelocateObservedFile(string previousPath, string currentPath, string verifiedDigest)
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
            history.Parameters.AddWithValue("$at", CatalogValues.Timestamp(clock.GetUtcNow()));
            history.ExecuteNonQuery();
            transaction.Commit();
            return true;
        });
    }
}
