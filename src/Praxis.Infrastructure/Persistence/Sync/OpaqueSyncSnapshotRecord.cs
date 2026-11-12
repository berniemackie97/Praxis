namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record OpaqueSyncSnapshotRecord(
    Guid RecordId, long Revision, bool Deleted, long ContentEpoch, byte[] Ciphertext);
