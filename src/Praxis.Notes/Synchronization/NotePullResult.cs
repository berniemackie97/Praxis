using Praxis.Notes.Journal;

namespace Praxis.Notes.Synchronization;

public sealed record NotePullResult(
    int ProtocolVersion, NotePullStatus Status, Guid SecuritySpaceId, Guid CollectionEpoch,
    Guid DeviceId,
    long RequestedCursor, long NextCursor, long HeadSequence,
    IReadOnlyList<AppliedNoteChange> Changes);
