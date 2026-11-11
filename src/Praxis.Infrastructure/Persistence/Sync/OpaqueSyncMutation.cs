namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record OpaqueSyncMutation(
    int ProtocolVersion, Guid SpaceId, Guid CollectionEpoch, Guid DeviceId,
    Guid OperationId, Guid RecordId, long ExpectedRevision, OpaqueSyncAction Action,
    long ContentEpoch, byte[] Ciphertext);
