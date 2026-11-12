namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record OpaqueSyncRotationReceipt(
    OpaqueSyncStatus Status, Guid SpaceId, Guid CollectionEpoch, Guid DeviceId,
    Guid RotationId, long HeadSequence, long ContentEpoch, long StagedRecords);
