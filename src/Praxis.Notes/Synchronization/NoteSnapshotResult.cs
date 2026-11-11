using Praxis.Notes.Journal;

namespace Praxis.Notes.Synchronization;

public sealed record NoteSnapshotResult(
    int ProtocolVersion, Guid SecuritySpaceId, Guid DeviceId, LocalNoteSnapshot Snapshot);
