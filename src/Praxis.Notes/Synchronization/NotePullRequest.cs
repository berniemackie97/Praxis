namespace Praxis.Notes.Synchronization;

public sealed record NotePullRequest(
    int ProtocolVersion, Guid SecuritySpaceId, Guid CollectionEpoch, Guid DeviceId,
    long AfterSequence, int Limit);
