using Microsoft.Data.Sqlite;
using Praxis.Infrastructure.Persistence.Security.Registry;

namespace Praxis.Infrastructure.Persistence.Sync;

public sealed partial class OpaqueSyncStore
{
    private (string CollectionEpoch, long Head, long ContentEpoch, string? LastRotation)
        RotationSpace(SqliteTransaction transaction, Guid spaceId)
    {
        using SqliteCommand load = Command(transaction, """
            SELECT collection_epoch, head_sequence, content_epoch, last_rotation_id
            FROM sync_spaces WHERE space_id=$space
            """, ("$space", Id(spaceId)));
        using SqliteDataReader reader = load.ExecuteReader();
        if (!reader.Read())
        {
            throw new UnauthorizedAccessException("The requested private resource is not authorized.");
        }

        return (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private OpaqueSyncRotationReceipt StageRotation(AuthorizedSpaceScope scope,
        OpaqueSyncRotationStage request)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        string space = Id(scope.SpaceId);
        (string epoch, long head, long currentEpoch, string? lastRotation) =
            RotationSpace(transaction, scope.SpaceId);
        OpaqueSyncRotationReceipt Reject(OpaqueSyncStatus status) =>
            new(status, request.SpaceId, request.CollectionEpoch, request.DeviceId,
                request.RotationId, 0, request.ContentEpoch, 0);

        if (request.CollectionEpoch.ToString("N") != epoch)
        {
            return Reject(OpaqueSyncStatus.WrongCollectionEpoch);
        }

        if (request.ProtocolVersion != 1)
        {
            return Reject(OpaqueSyncStatus.UnsupportedProtocolVersion);
        }

        if (request.RotationId == Guid.Empty || request.PreviousEpoch < 1 ||
            request.PreviousEpoch == long.MaxValue ||
            request.ContentEpoch != request.PreviousEpoch + 1 ||
            request.ContentEpoch != scope.ContentEpoch ||
            request.ExpectedHead <= 0 || request.ExpectedHead != head ||
            request.Record is not { } record || record.RecordId == Guid.Empty ||
            record.Revision < 1 || record.ContentEpoch != request.ContentEpoch ||
            record.Ciphertext is null || record.Ciphertext.Length is < 29 or > MaxPayloadBytes)
        {
            return Reject(OpaqueSyncStatus.InvalidRequest);
        }

        if (lastRotation == Id(request.RotationId) && currentEpoch == scope.ContentEpoch)
        {
            return new OpaqueSyncRotationReceipt(OpaqueSyncStatus.Applied,
                request.SpaceId, request.CollectionEpoch, request.DeviceId,
                request.RotationId, head, scope.ContentEpoch, 0);
        }

        if (currentEpoch != 0 && currentEpoch != request.PreviousEpoch)
        {
            return Reject(OpaqueSyncStatus.ResetRequired);
        }

        using (SqliteCommand current = Command(transaction, """
            SELECT revision, deleted, key_epoch FROM sync_records
            WHERE space_id=$space AND record_id=$record
            """, ("$space", space), ("$record", Id(record.RecordId))))
        using (SqliteDataReader reader = current.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt64(0) != record.Revision ||
                (reader.GetInt64(1) != 0) != record.Deleted ||
                reader.GetInt64(2) != request.PreviousEpoch)
            {
                return Reject(OpaqueSyncStatus.InvalidRequest);
            }
        }

        using (SqliteCommand existing = Command(transaction, """
            SELECT rotation_id, expected_head, old_epoch, new_epoch
            FROM sync_rotation WHERE space_id=$space
            """, ("$space", space)))
        using (SqliteDataReader reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != Id(request.RotationId) ||
                    reader.GetInt64(1) != head ||
                    reader.GetInt64(2) != request.PreviousEpoch ||
                    reader.GetInt64(3) != request.ContentEpoch)
                {
                    return Reject(OpaqueSyncStatus.OperationKeyReused);
                }
            }
            else
            {
                reader.Close();
                using SqliteCommand begin = Command(transaction, """
                    INSERT INTO sync_rotation
                        (space_id, rotation_id, expected_head, old_epoch, new_epoch)
                    VALUES ($space, $rotation, $head, $old, $new)
                    """, ("$space", space), ("$rotation", Id(request.RotationId)),
                    ("$head", head), ("$old", request.PreviousEpoch),
                    ("$new", request.ContentEpoch));
                begin.ExecuteNonQuery();
            }
        }

        using (SqliteCommand stage = Command(transaction, """
            INSERT INTO sync_rotation_records
                (space_id, record_id, revision, deleted, ciphertext)
            VALUES ($space, $record, $revision, $deleted, $cipher)
            ON CONFLICT(space_id, record_id) DO UPDATE SET
                ciphertext=excluded.ciphertext
            WHERE sync_rotation_records.revision=excluded.revision
              AND sync_rotation_records.deleted=excluded.deleted
            """,
            ("$space", space), ("$record", Id(record.RecordId)),
            ("$revision", record.Revision), ("$deleted", record.Deleted ? 1 : 0),
            ("$cipher", record.Ciphertext)))
        {
            if (stage.ExecuteNonQuery() != 1)
            {
                return Reject(OpaqueSyncStatus.InvalidRequest);
            }
        }

        using SqliteCommand budget = Command(transaction, """
            SELECT COUNT(*), COALESCE(SUM(length(ciphertext)), 0)
            FROM sync_rotation_records WHERE space_id=$space
            """, ("$space", space));
        using SqliteDataReader staged = budget.ExecuteReader();
        staged.Read();
        long count = staged.GetInt64(0);
        if (count > MaximumSnapshotRecords ||
            staged.GetInt64(1) > MaximumSnapshotCiphertextBytes)
        {
            return Reject(OpaqueSyncStatus.QuotaExceeded);
        }

        staged.Close();
        transaction.Commit();
        return new OpaqueSyncRotationReceipt(OpaqueSyncStatus.Ok, request.SpaceId,
            request.CollectionEpoch, request.DeviceId, request.RotationId,
            head, scope.ContentEpoch, count);
    }

    private OpaqueSyncRotationReceipt CommitRotation(AuthorizedSpaceScope scope,
        OpaqueSyncRotationCommit request)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        string space = Id(scope.SpaceId);
        (string epoch, long head, long currentEpoch, string? lastRotation) =
            RotationSpace(transaction, scope.SpaceId);
        OpaqueSyncRotationReceipt Reject(OpaqueSyncStatus status) =>
            new(status, request.SpaceId, request.CollectionEpoch, request.DeviceId,
                request.RotationId, 0, request.ContentEpoch, 0);

        if (request.CollectionEpoch.ToString("N") != epoch)
        {
            return Reject(OpaqueSyncStatus.WrongCollectionEpoch);
        }

        if (request.ProtocolVersion != 1)
        {
            return Reject(OpaqueSyncStatus.UnsupportedProtocolVersion);
        }

        if (request.RotationId == Guid.Empty || request.PreviousEpoch < 1 ||
            request.PreviousEpoch == long.MaxValue ||
            request.ContentEpoch != request.PreviousEpoch + 1 ||
            request.ContentEpoch != scope.ContentEpoch ||
            request.ExpectedHead <= 0 || request.ExpectedHead != head)
        {
            return Reject(OpaqueSyncStatus.InvalidRequest);
        }

        if (lastRotation == Id(request.RotationId) && currentEpoch == scope.ContentEpoch)
        {
            return new OpaqueSyncRotationReceipt(OpaqueSyncStatus.Applied,
                request.SpaceId, request.CollectionEpoch, request.DeviceId,
                request.RotationId, head, scope.ContentEpoch, 0);
        }

        if (currentEpoch != 0 && currentEpoch != request.PreviousEpoch)
        {
            return Reject(OpaqueSyncStatus.ResetRequired);
        }

        using (SqliteCommand active = Command(transaction, """
            SELECT rotation_id, expected_head, old_epoch, new_epoch
            FROM sync_rotation WHERE space_id=$space
            """, ("$space", space)))
        using (SqliteDataReader reader = active.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != Id(request.RotationId) ||
                reader.GetInt64(1) != head ||
                reader.GetInt64(2) != request.PreviousEpoch ||
                reader.GetInt64(3) != request.ContentEpoch)
            {
                return Reject(OpaqueSyncStatus.InvalidRequest);
            }
        }

        long recordCount;
        using (SqliteCommand counts = Command(transaction, """
            SELECT
                (SELECT COUNT(*) FROM sync_records WHERE space_id=$space),
                (SELECT COUNT(*) FROM sync_rotation_records WHERE space_id=$space),
                (SELECT COALESCE(SUM(length(ciphertext)), 0)
                    FROM sync_rotation_records WHERE space_id=$space)
            """, ("$space", space)))
        using (SqliteDataReader reader = counts.ExecuteReader())
        {
            reader.Read();
            recordCount = reader.GetInt64(0);
            if (recordCount == 0 || recordCount > MaximumSnapshotRecords ||
                reader.GetInt64(1) != recordCount ||
                reader.GetInt64(2) > MaximumSnapshotCiphertextBytes)
            {
                return Reject(OpaqueSyncStatus.InvalidRequest);
            }
        }

        using (SqliteCommand mismatch = Command(transaction, """
            SELECT 1 FROM sync_records r
            LEFT JOIN sync_rotation_records s
                ON s.space_id=r.space_id AND s.record_id=r.record_id
            WHERE r.space_id=$space AND
                (r.key_epoch<>$old OR s.record_id IS NULL OR
                 s.revision<>r.revision OR s.deleted<>r.deleted)
            LIMIT 1
            """, ("$space", space), ("$old", request.PreviousEpoch)))
        {
            if (mismatch.ExecuteScalar() is not null)
            {
                return Reject(OpaqueSyncStatus.ResetRequired);
            }
        }

        using (SqliteCommand events = Command(transaction, """
            SELECT 1 FROM sync_events
            WHERE space_id=$space AND key_epoch<>$old LIMIT 1
            """, ("$space", space), ("$old", request.PreviousEpoch)))
        {
            if (events.ExecuteScalar() is not null)
            {
                return Reject(OpaqueSyncStatus.ResetRequired);
            }
        }

        using (SqliteCommand update = Command(transaction, """
            UPDATE sync_records SET
                key_epoch=$new,
                ciphertext=(SELECT s.ciphertext FROM sync_rotation_records s
                    WHERE s.space_id=sync_records.space_id
                    AND s.record_id=sync_records.record_id)
            WHERE space_id=$space AND key_epoch=$old
            """, ("$space", space), ("$old", request.PreviousEpoch),
            ("$new", request.ContentEpoch)))
        {
            if (update.ExecuteNonQuery() != recordCount)
            {
                throw new InvalidDataException("The encrypted migration did not replace every record.");
            }
        }

        using (SqliteCommand clear = Command(transaction,
            "DELETE FROM sync_events WHERE space_id=$space", ("$space", space)))
        {
            clear.ExecuteNonQuery();
        }

        using (SqliteCommand advance = Command(transaction, """
            UPDATE sync_spaces
            SET history_floor=$head, content_epoch=$new, last_rotation_id=$rotation
            WHERE space_id=$space AND head_sequence=$head
            """, ("$space", space), ("$head", head),
            ("$new", request.ContentEpoch), ("$rotation", Id(request.RotationId))))
        {
            if (advance.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException("The encrypted migration head changed.");
            }
        }

        using (SqliteCommand cleanup = Command(transaction,
            "DELETE FROM sync_rotation WHERE space_id=$space", ("$space", space)))
        {
            cleanup.ExecuteNonQuery();
        }

        transaction.Commit();
        return new OpaqueSyncRotationReceipt(OpaqueSyncStatus.Applied,
            request.SpaceId, request.CollectionEpoch, request.DeviceId,
            request.RotationId, head, scope.ContentEpoch, recordCount);
    }
}
