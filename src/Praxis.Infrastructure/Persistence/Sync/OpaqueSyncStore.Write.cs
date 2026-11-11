using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Praxis.Infrastructure.Persistence.Sync;

public sealed partial class OpaqueSyncStore
{
    private OpaqueSyncReceipt Write(
        Praxis.Infrastructure.Persistence.Security.Registry.AuthorizedSpaceScope scope,
        OpaqueSyncMutation request)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        string space = Id(scope.SpaceId);
        long head;
        string epoch;
        using (SqliteCommand load = Command(transaction, """
            SELECT collection_epoch, head_sequence FROM sync_spaces
            WHERE space_id=$space
            """, ("$space", space)))
        using (SqliteDataReader reader = load.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new UnauthorizedAccessException("The requested private resource is not authorized.");
            }

            epoch = reader.GetString(0);
            head = reader.GetInt64(1);
        }

        OpaqueSyncReceipt Reject(OpaqueSyncStatus status, long revision = 0) =>
            new(status, request.SpaceId, request.CollectionEpoch, request.DeviceId,
                request.OperationId, head, revision);

        if (request.CollectionEpoch.ToString("N") != epoch)
        {
            return Reject(OpaqueSyncStatus.WrongCollectionEpoch);
        }

        if (request.ProtocolVersion != 1)
        {
            return Reject(OpaqueSyncStatus.UnsupportedProtocolVersion);
        }

        byte[]? payload = request.Ciphertext?.ToArray();
        if (request.OperationId == Guid.Empty || request.RecordId == Guid.Empty ||
            request.ExpectedRevision < 0 || !Enum.IsDefined(request.Action) ||
            request.ContentEpoch != scope.ContentEpoch ||
            (request.Action == OpaqueSyncAction.Delete
                ? payload is not { Length: 0 }
                : payload is not { Length: >= 28 and <= MaxPayloadBytes }))
        {
            return Reject(OpaqueSyncStatus.InvalidRequest);
        }

        byte[] digest = HashRequest(request, payload!);
        using (SqliteCommand prior = Command(transaction, """
            SELECT request_hash, sequence, revision
            FROM sync_receipts
            WHERE space_id=$space AND device_id=$device AND operation_id=$operation
            """, ("$space", space), ("$device", Id(scope.DeviceId)),
            ("$operation", Id(request.OperationId))))
        using (SqliteDataReader reader = prior.ExecuteReader())
        {
            if (reader.Read())
            {
                OpaqueSyncStatus status = CryptographicOperations.FixedTimeEquals(
                    digest, (byte[])reader.GetValue(0))
                    ? OpaqueSyncStatus.Applied : OpaqueSyncStatus.OperationKeyReused;
                return new OpaqueSyncReceipt(status, request.SpaceId,
                    request.CollectionEpoch, request.DeviceId, request.OperationId,
                    reader.GetInt64(1), reader.GetInt64(2));
            }
        }

        long revision = 0;
        bool exists = false;
        bool deleted = false;
        byte[] current = [];
        using (SqliteCommand record = Command(transaction, """
            SELECT space_id, revision, deleted, ciphertext
            FROM sync_records WHERE record_id=$record
            """, ("$record", Id(request.RecordId))))
        using (SqliteDataReader reader = record.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != space)
                {
                    return Reject(OpaqueSyncStatus.InvalidRequest);
                }

                exists = true;
                revision = reader.GetInt64(1);
                deleted = reader.GetInt64(2) != 0;
                current = (byte[])reader.GetValue(3);
            }
        }

        if (request.ExpectedRevision != revision ||
            (!exists && request.Action != OpaqueSyncAction.Upsert) ||
            (exists && deleted && request.Action != OpaqueSyncAction.Restore) ||
            (exists && !deleted && request.Action == OpaqueSyncAction.Restore))
        {
            return Reject(OpaqueSyncStatus.StaleRevision, revision);
        }

        using (SqliteCommand budget = Command(transaction, """
            SELECT (SELECT COUNT(*) FROM sync_events WHERE space_id=$space),
                   (SELECT COALESCE(SUM(length(ciphertext)), 0)
                    FROM sync_events WHERE space_id=$space)
                 + (SELECT COALESCE(SUM(length(ciphertext)), 0)
                    FROM sync_records WHERE space_id=$space)
            """, ("$space", space)))
        using (SqliteDataReader reader = budget.ExecuteReader())
        {
            reader.Read();
            long stored = reader.GetInt64(1);
            int acceptedBytes = request.Action == OpaqueSyncAction.Delete
                ? current.Length : payload!.Length;
            if (reader.GetInt64(0) >= MaximumEvents ||
                stored + acceptedBytes * 2L > MaximumStoredBytes)
            {
                return Reject(OpaqueSyncStatus.QuotaExceeded);
            }
        }

        long nextSequence = checked(head + 1);
        long nextRevision = checked(revision + 1);
        bool nextDeleted = request.Action == OpaqueSyncAction.Delete;
        byte[] saved = nextDeleted ? current : payload!;

        using (SqliteCommand write = Command(transaction, """
            INSERT INTO sync_records
                (record_id, space_id, revision, deleted, key_epoch, ciphertext)
            VALUES ($record, $space, $revision, $deleted, $key, $cipher)
            ON CONFLICT(record_id) DO UPDATE SET
                revision=excluded.revision, deleted=excluded.deleted,
                key_epoch=excluded.key_epoch, ciphertext=excluded.ciphertext
            WHERE sync_records.space_id=excluded.space_id
            """,
            ("$record", Id(request.RecordId)), ("$space", space),
            ("$revision", nextRevision), ("$deleted", nextDeleted ? 1 : 0),
            ("$key", scope.ContentEpoch), ("$cipher", saved)))
        {
            if (write.ExecuteNonQuery() != 1)
            {
                return Reject(OpaqueSyncStatus.InvalidRequest);
            }
        }

        using (SqliteCommand append = Command(transaction, """
            INSERT INTO sync_events
                (space_id, sequence, record_id, revision, deleted, key_epoch, ciphertext)
            VALUES ($space, $sequence, $record, $revision, $deleted, $key, $cipher)
            """,
            ("$space", space), ("$sequence", nextSequence),
            ("$record", Id(request.RecordId)), ("$revision", nextRevision),
            ("$deleted", nextDeleted ? 1 : 0),
            ("$key", scope.ContentEpoch), ("$cipher", saved)))
        {
            append.ExecuteNonQuery();
        }

        using (SqliteCommand receipt = Command(transaction, """
            INSERT INTO sync_receipts
                (space_id, device_id, operation_id, request_hash, sequence, revision)
            VALUES ($space, $device, $operation, $digest, $sequence, $revision)
            """,
            ("$space", space), ("$device", Id(scope.DeviceId)),
            ("$operation", Id(request.OperationId)), ("$digest", digest),
            ("$sequence", nextSequence), ("$revision", nextRevision)))
        {
            receipt.ExecuteNonQuery();
        }

        using (SqliteCommand advance = Command(transaction, """
            UPDATE sync_spaces SET head_sequence=$next
            WHERE space_id=$space AND head_sequence=$old
            """, ("$next", nextSequence), ("$space", space), ("$old", head)))
        {
            if (advance.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException("The sync sequence changed during its transaction.");
            }
        }

        transaction.Commit();
        return new OpaqueSyncReceipt(OpaqueSyncStatus.Applied, request.SpaceId,
            request.CollectionEpoch, request.DeviceId, request.OperationId,
            nextSequence, nextRevision);
    }

    private static byte[] HashRequest(OpaqueSyncMutation request, byte[] cipher)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(request.ProtocolVersion);
            writer.Write(request.SpaceId.ToByteArray());
            writer.Write(request.CollectionEpoch.ToByteArray());
            writer.Write(request.DeviceId.ToByteArray());
            writer.Write(request.OperationId.ToByteArray());
            writer.Write(request.RecordId.ToByteArray());
            writer.Write(request.ExpectedRevision);
            writer.Write((int)request.Action);
            writer.Write(request.ContentEpoch);
            writer.Write(cipher.Length);
            writer.Write(cipher);
        }

        return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }
}
