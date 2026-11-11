using Praxis.Notes.Journal;

namespace Praxis.Notes.Synchronization;

public sealed record NoteSubmitRequest(
    int ProtocolVersion, Guid SecuritySpaceId, Guid CollectionEpoch, Guid DeviceId,
    PendingNoteEdit Edit);
