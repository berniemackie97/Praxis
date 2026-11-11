using Microsoft.Data.Sqlite;

namespace Praxis.Infrastructure.Persistence.Sync;

public sealed partial class OpaqueSyncStore
{
    private OpaqueSyncPage Read(OpaqueSyncPull request)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        string space = Id(request.SpaceId);
        string epoch;
        long head;
        long floor;
        using (SqliteCommand load = Command(transaction, """
            SELECT collection_epoch, head_sequence, history_floor
            FROM sync_spaces WHERE space_id=$space
            """, ("$space", space)))
        using (SqliteDataReader reader = load.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new UnauthorizedAccessException("The requested private resource is not authorized.");
            }

            epoch = reader.GetString(0);
            head = reader.GetInt64(1);
            floor = reader.GetInt64(2);
        }

        OpaqueSyncPage Reject(OpaqueSyncStatus status) =>
            new(status, request.SpaceId, request.CollectionEpoch, request.DeviceId,
                request.AfterSequence, request.AfterSequence, head, []);

        if (request.CollectionEpoch.ToString("N") != epoch)
        {
            return Reject(OpaqueSyncStatus.WrongCollectionEpoch);
        }

        if (request.ProtocolVersion != 1)
        {
            return Reject(OpaqueSyncStatus.UnsupportedProtocolVersion);
        }

        if (request.Limit is < 1 or > 100 ||
            request.AfterSequence < 0 || request.AfterSequence > head)
        {
            return Reject(OpaqueSyncStatus.InvalidCursor);
        }

        if (request.AfterSequence < floor)
        {
            return Reject(OpaqueSyncStatus.CursorExpired);
        }

        using (SqliteCommand epochCheck = Command(transaction, """
            SELECT 1 FROM (
                SELECT key_epoch FROM sync_records WHERE space_id=$space
                UNION ALL SELECT key_epoch FROM sync_events WHERE space_id=$space
            ) WHERE key_epoch<>$epoch LIMIT 1
            """, ("$space", space), ("$epoch", request.ContentEpoch)))
        {
            if (epochCheck.ExecuteScalar() is not null)
            {
                return Reject(OpaqueSyncStatus.ResetRequired);
            }
        }

        List<OpaqueSyncEvent> changes = [];
        using (SqliteCommand command = Command(transaction, """
            SELECT sequence, record_id, revision, deleted, key_epoch, ciphertext
            FROM sync_events WHERE space_id=$space AND sequence>$cursor
            ORDER BY sequence LIMIT $limit
            """, ("$space", space), ("$cursor", request.AfterSequence),
            ("$limit", request.Limit)))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                changes.Add(new OpaqueSyncEvent(reader.GetInt64(0),
                    Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
                    reader.GetInt64(3) != 0, reader.GetInt64(4),
                    (byte[])reader.GetValue(5)));
            }
        }

        transaction.Commit();
        long next = changes.Count == 0 ? request.AfterSequence : changes[^1].Sequence;
        return new OpaqueSyncPage(OpaqueSyncStatus.Ok, request.SpaceId,
            request.CollectionEpoch, request.DeviceId, request.AfterSequence,
            next, head, changes.AsReadOnly());
    }
}
