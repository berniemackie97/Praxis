namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record OpaqueSyncRotationCommit(
    int ProtocolVersion, Guid SpaceId, Guid CollectionEpoch, Guid DeviceId,
    Guid RotationId, long ExpectedHead, long PreviousEpoch, long ContentEpoch);
