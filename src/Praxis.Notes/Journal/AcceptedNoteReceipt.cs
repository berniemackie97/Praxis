namespace Praxis.Notes.Journal;

public sealed record AcceptedNoteReceipt(Guid OperationId, long Sequence, LocalNoteState Record);
