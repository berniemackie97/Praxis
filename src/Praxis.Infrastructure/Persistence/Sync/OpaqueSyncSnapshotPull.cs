namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record OpaqueSyncSnapshotPull(
    int ProtocolVersion, Guid SpaceId, Guid CollectionEpoch, Guid DeviceId,
    long ContentEpoch, long ExpectedHead, Guid AfterRecordId, int Limit);
