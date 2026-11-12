using Microsoft.Data.Sqlite;

namespace Praxis.Infrastructure.Persistence.Sync;

public sealed partial class OpaqueSyncStore
{
    private const int MaximumSnapshotPage = 16;

    private OpaqueSyncSnapshotPage ReadSnapshot(OpaqueSyncSnapshotPull request)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        string space = Id(request.SpaceId);
        string epoch;
        long head;
        using (SqliteCommand read = Command(transaction, """
            SELECT collection_epoch, head_sequence FROM sync_spaces WHERE space_id=$space
            """, ("$space", space)))
        using (SqliteDataReader reader = read.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw new UnauthorizedAccessException("The requested private resource is not authorized.");
            }

            epoch = reader.GetString(0);
            head = reader.GetInt64(1);
        }

        OpaqueSyncSnapshotPage Reject(OpaqueSyncStatus status) =>
            new(status, request.SpaceId, request.CollectionEpoch, request.DeviceId,
                request.ExpectedHead, request.AfterRecordId, 0, 0, []);

        if (request.CollectionEpoch.ToString("N") != epoch)
        {
            return Reject(OpaqueSyncStatus.WrongCollectionEpoch);
        }

        if (request.ProtocolVersion != 1)
        {
            return Reject(OpaqueSyncStatus.UnsupportedProtocolVersion);
        }

        if (request.ExpectedHead < 0 || request.ExpectedHead > head ||
            request.Limit is < 1 or > MaximumSnapshotPage ||
            (request.AfterRecordId != Guid.Empty && request.ExpectedHead == 0))
        {
            return Reject(OpaqueSyncStatus.InvalidCursor);
        }

        if (request.ExpectedHead != 0 && request.ExpectedHead != head)
        {
            return Reject(OpaqueSyncStatus.InvalidCursor);
        }

        using (SqliteCommand check = Command(transaction, """
            SELECT 1 FROM (
                SELECT key_epoch FROM sync_records WHERE space_id=$space
                UNION ALL SELECT key_epoch FROM sync_events WHERE space_id=$space
            ) WHERE key_epoch<>$epoch LIMIT 1
            """, ("$space", space), ("$epoch", request.ContentEpoch)))
        {
            if (check.ExecuteScalar() is not null)
            {
                return Reject(OpaqueSyncStatus.ResetRequired);
            }
        }

        long total;
        using (SqliteCommand count = Command(transaction,
            "SELECT COUNT(*) FROM sync_records WHERE space_id=$space", ("$space", space)))
        {
            total = Convert.ToInt64(count.ExecuteScalar());
        }

        List<OpaqueSyncSnapshotRecord> records = [];
        using (SqliteCommand read = Command(transaction, """
            SELECT record_id, revision, deleted, key_epoch, ciphertext
            FROM sync_records
            WHERE space_id=$space AND record_id>$after
            ORDER BY record_id LIMIT $limit
            """, ("$space", space),
            ("$after", request.AfterRecordId == Guid.Empty
                ? "" : Id(request.AfterRecordId)), ("$limit", request.Limit)))
        using (SqliteDataReader reader = read.ExecuteReader())
        {
            while (reader.Read())
            {
                records.Add(new OpaqueSyncSnapshotRecord(Guid.Parse(reader.GetString(0)),
                    reader.GetInt64(1), reader.GetInt64(2) != 0,
                    reader.GetInt64(3), (byte[])reader.GetValue(4)));
            }
        }

        transaction.Commit();
        return new OpaqueSyncSnapshotPage(OpaqueSyncStatus.Ok, request.SpaceId,
            request.CollectionEpoch, request.DeviceId, request.ExpectedHead,
            request.AfterRecordId, head, total, records.AsReadOnly());
    }

    // Host-operator maintenance: retain the current record projection and durable receipts.
    public void PruneThrough(Guid spaceId, long sequence)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        lock (gate)
        {
            EnsureOpen();
            using SqliteTransaction transaction = connection.BeginTransaction();
            string space = Id(spaceId);
            long head;
            long floor;
            using (SqliteCommand read = Command(transaction, """
                SELECT head_sequence, history_floor FROM sync_spaces WHERE space_id=$space
                """, ("$space", space)))
            using (SqliteDataReader reader = read.ExecuteReader())
            {
                if (!reader.Read())
                {
                    throw new InvalidOperationException("The synchronization space is not provisioned.");
                }

                head = reader.GetInt64(0);
                floor = reader.GetInt64(1);
            }

            if (sequence < floor || sequence > head)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence));
            }

            using (SqliteCommand remove = Command(transaction, """
                DELETE FROM sync_events WHERE space_id=$space AND sequence<=$through
                """, ("$space", space), ("$through", sequence)))
            {
                remove.ExecuteNonQuery();
            }

            using (SqliteCommand advance = Command(transaction, """
                UPDATE sync_spaces SET history_floor=$through WHERE space_id=$space
                """, ("$space", space), ("$through", sequence)))
            {
                advance.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }
}
