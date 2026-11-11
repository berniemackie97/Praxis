namespace Praxis.Notes.Journal;

public sealed record PendingNoteEdit(Guid OperationId, Guid RecordId, long ExpectedRevision, LocalNoteAction Action, string? Path, string? Markdown);
