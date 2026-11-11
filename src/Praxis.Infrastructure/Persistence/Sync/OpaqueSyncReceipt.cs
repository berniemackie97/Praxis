namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record OpaqueSyncReceipt(
    OpaqueSyncStatus Status, Guid SpaceId, Guid CollectionEpoch, Guid DeviceId,
    Guid OperationId, long Sequence, long Revision);
