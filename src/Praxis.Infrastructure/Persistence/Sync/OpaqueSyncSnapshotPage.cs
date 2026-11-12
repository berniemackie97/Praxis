namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record OpaqueSyncSnapshotPage(
    OpaqueSyncStatus Status, Guid SpaceId, Guid CollectionEpoch, Guid DeviceId,
    long RequestedHead, Guid AfterRecordId, long HeadSequence,
    long TotalRecords, IReadOnlyList<OpaqueSyncSnapshotRecord> Records);
