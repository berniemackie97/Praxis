using Praxis.Notes.Journal;

namespace Praxis.Notes.Synchronization;

public sealed record NoteSubmitResult(
    int ProtocolVersion, NoteSubmitStatus Status, Guid SecuritySpaceId, Guid CollectionEpoch,
    Guid DeviceId, Guid OperationId, long Sequence, LocalNoteState? Current);
