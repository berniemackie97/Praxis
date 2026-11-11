namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record OpaqueSyncEvent(
    long Sequence, Guid RecordId, long Revision, bool Deleted,
    long ContentEpoch, byte[] Ciphertext);
