namespace Praxis.Notes.Journal;

public sealed record IncomingNoteCollision(LocalNoteState Remote, Guid BlockingRecordId);
