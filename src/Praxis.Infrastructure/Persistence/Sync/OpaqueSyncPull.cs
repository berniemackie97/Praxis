namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record OpaqueSyncPull(
    int ProtocolVersion, Guid SpaceId, Guid CollectionEpoch, Guid DeviceId,
    long ContentEpoch, long AfterSequence, int Limit);
