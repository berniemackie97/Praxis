namespace Praxis.Notes.Journal;

public sealed record LocalNoteState(Guid Id, long Revision, string Path, string Markdown, bool Deleted);
