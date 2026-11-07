namespace Praxis.Knowledge;

public sealed class NoteRecord
{
    private readonly NoteRevision[] revisions;

    private NoteRecord(Guid id, NoteRevision[] revisions)
    {
        if (id == Guid.Empty || revisions.Length == 0)
        {
            throw new ArgumentException("A note needs an identity and at least one revision.");
        }

        for (var index = 0; index < revisions.Length; index++)
        {
            var revision = revisions[index];
            if (revision is null || revision.Number != index + 1)
            {
                throw new ArgumentException("Note revisions must be contiguous and start at one.");
            }

            NotePath.Validate(revision.Path);
            ArgumentNullException.ThrowIfNull(revision.Markdown);
        }

        Id = id;
        this.revisions = revisions;
        Revisions = Array.AsReadOnly(revisions);
    }

    public Guid Id { get; }

    public IReadOnlyList<NoteRevision> Revisions { get; }

    public NoteRevision Current => revisions[^1];

    public static NoteRecord Create(string path, string markdown)
    {
        NotePath.Validate(path);
        ArgumentNullException.ThrowIfNull(markdown);
        return new NoteRecord(Guid.NewGuid(), [new NoteRevision(1, path, markdown)]);
    }

    public NoteRecord Revise(string path, string markdown)
    {
        NotePath.Validate(path);
        ArgumentNullException.ThrowIfNull(markdown);

        var next = new NoteRevision(checked(Current.Number + 1), path, markdown);
        var updated = new NoteRevision[revisions.Length + 1];
        Array.Copy(revisions, updated, revisions.Length);
        updated[^1] = next;
        return new NoteRecord(Id, updated);
    }

    internal static NoteRecord Restore(Guid id, NoteRevision[] revisions) => new(id, revisions);
}
