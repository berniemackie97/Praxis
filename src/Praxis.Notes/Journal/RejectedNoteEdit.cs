namespace Praxis.Notes.Journal;

public sealed record RejectedNoteEdit(PendingNoteEdit Edit, LocalNoteConflictReason Reason, LocalNoteState? Current);
