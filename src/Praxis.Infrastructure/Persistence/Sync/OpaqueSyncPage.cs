namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record OpaqueSyncPage(
    OpaqueSyncStatus Status, Guid SpaceId, Guid CollectionEpoch, Guid DeviceId,
    long RequestedCursor, long NextCursor, long HeadSequence,
    IReadOnlyList<OpaqueSyncEvent> Changes);
