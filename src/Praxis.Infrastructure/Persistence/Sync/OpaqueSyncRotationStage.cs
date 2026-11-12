namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record OpaqueSyncRotationStage(
    int ProtocolVersion, Guid SpaceId, Guid CollectionEpoch, Guid DeviceId,
    Guid RotationId, long ExpectedHead, long PreviousEpoch, long ContentEpoch,
    OpaqueSyncSnapshotRecord Record);
