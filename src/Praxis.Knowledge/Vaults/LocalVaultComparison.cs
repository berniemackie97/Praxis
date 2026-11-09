namespace Praxis.Knowledge;

public sealed record VaultFileDifference(string Path, string Reason);

public sealed record VaultComparison(int MatchedFiles, IReadOnlyList<VaultFileDifference> Differences)
{
    public bool IsMatch => Differences.Count == 0;
}

public static class LocalVaultComparison
{
    public static VaultComparison CompareArchive(string archivePath, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var expected = NoteArchive.ReadCollection(stream);
        var actual = LocalVaultExchange.Capture(directory);

        var actualNotes = actual.Notes.ToDictionary(note => note.Current.Path, StringComparer.Ordinal);
        var actualAttachments = actual.Attachments.ToDictionary(item => item.Path, StringComparer.Ordinal);
        var differences = new List<VaultFileDifference>();
        var matched = 0;

        foreach (var note in expected.Notes)
        {
            var path = note.Current.Path;
            if (!actualNotes.Remove(path, out var observed))
            {
                differences.Add(new VaultFileDifference(path, "missing"));
            }
            else if (!string.Equals(note.Current.Markdown, observed.Current.Markdown, StringComparison.Ordinal))
            {
                differences.Add(new VaultFileDifference(path, "changed"));
            }
            else
            {
                matched++;
            }
        }

        foreach (var attachment in expected.Attachments)
        {
            if (!actualAttachments.Remove(attachment.Path, out var observed))
            {
                differences.Add(new VaultFileDifference(attachment.Path, "missing"));
            }
            else if (!attachment.ContentSpan.SequenceEqual(observed.ContentSpan))
            {
                differences.Add(new VaultFileDifference(attachment.Path, "changed"));
            }
            else
            {
                matched++;
            }
        }

        differences.AddRange(actualNotes.Keys.Select(path => new VaultFileDifference(path, "unexpected")));
        differences.AddRange(actualAttachments.Keys.Select(path => new VaultFileDifference(path, "unexpected")));
        differences.Sort((a, b) =>
        {
            var path = StringComparer.Ordinal.Compare(a.Path, b.Path);
            return path != 0 ? path : StringComparer.Ordinal.Compare(a.Reason, b.Reason);
        });

        return new VaultComparison(matched, Array.AsReadOnly(differences.ToArray()));
    }
}
