namespace Praxis.Notes.Journal;

public sealed record LocalNoteSnapshot(Guid CollectionEpoch, long Cursor, IReadOnlyList<LocalNoteState> Records);
