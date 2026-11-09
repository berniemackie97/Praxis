namespace Praxis.Knowledge;

public sealed record VaultRename(string FromPath, string ToPath, bool IsFolder = false);

public sealed record VaultChange(
    Guid RecordId, string? PreviousPath, string CurrentPath,
    bool IsAttachment, bool ContentChanged, bool PathChanged);

public sealed record VaultUpdate(KnowledgeCollection Collection, IReadOnlyList<VaultChange> Changes,
    int AddedLinks = 0, IReadOnlyList<AttachmentReference>? RemovedLinkPairs = null)
{
    public int RemovedLinks => RemovedLinkPairs?.Count ?? 0;
}

public static class LocalVaultReconciler
{
    public static VaultUpdate Reconcile(
        KnowledgeCollection previous, KnowledgeCollection captured,
        IReadOnlyList<VaultRename>? renames = null,
        bool trackLinks = false, bool previewLinkRemovals = false)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(captured);

        if (captured.References.Count != 0 ||
            captured.Notes.Any(note => note.Revisions.Count != 1))
        {
            throw new ArgumentException("The observed vault must be a fresh filesystem capture.", nameof(captured));
        }

        var priorByPath = new Dictionary<string, PriorFile>(StringComparer.OrdinalIgnoreCase);
        var priorNotes = previous.Notes.ToDictionary(note => note.Id);
        var priorAttachments = previous.Attachments.ToDictionary(attachment => attachment.Id);
        foreach (var note in previous.Notes)
        {
            priorByPath.Add(note.Current.Path, new PriorFile(note.Id, note.Current.Path, false));
        }

        foreach (var attachment in previous.Attachments)
        {
            priorByPath.Add(attachment.Path, new PriorFile(attachment.Id, attachment.Path, true));
        }

        var capturedPaths = captured.Notes.Select(note => note.Current.Path)
            .Concat(captured.Attachments.Select(attachment => attachment.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remapped = new Dictionary<string, PriorFile>(StringComparer.OrdinalIgnoreCase);
        var remappedSources = new HashSet<PriorFile>();
        var rules = renames ?? [];
        var mappings = rules.Any(rule => rule?.IsFolder == true)
            ? ExpandMoves(priorByPath, rules) : rules;
        foreach (var rule in mappings)
        {
            if (rule is null)
            {
                throw new ArgumentException("The rename list contains an empty mapping.", nameof(renames));
            }

            PortablePath.Validate(rule.FromPath);
            PortablePath.Validate(rule.ToPath);
            if (string.Equals(rule.FromPath, rule.ToPath, StringComparison.Ordinal) ||
                !priorByPath.TryGetValue(rule.FromPath, out var origin) ||
                !capturedPaths.Contains(rule.ToPath) ||
                !remappedSources.Add(origin) ||
                !remapped.TryAdd(rule.ToPath, origin))
            {
                throw new InvalidDataException("The rename mapping is redundant, missing or ambiguous.");
            }
        }

        var visited = new HashSet<PriorFile>();
        var notes = new List<NoteRecord>(captured.Notes.Count);
        var attachments = new List<AttachmentRecord>(captured.Attachments.Count);
        var changes = new List<VaultChange>();

        foreach (var note in captured.Notes)
        {
            var path = note.Current.Path;
            var prior = Find(path, isAttachment: false);
            if (prior is null)
            {
                notes.Add(note);
                changes.Add(new VaultChange(note.Id, null, path, false, true, false));
                continue;
            }

            var original = priorNotes[prior.Id];
            var pathChanged = !string.Equals(original.Current.Path, path, StringComparison.Ordinal);
            var bodyChanged = !string.Equals(original.Current.Markdown, note.Current.Markdown, StringComparison.Ordinal);
            notes.Add(pathChanged || bodyChanged
                ? original.Revise(path, note.Current.Markdown) : original);
            if (pathChanged || bodyChanged)
            {
                changes.Add(new VaultChange(original.Id, original.Current.Path,
                    path, false, bodyChanged, pathChanged));
            }
        }

        foreach (var attachment in captured.Attachments)
        {
            var prior = Find(attachment.Path, isAttachment: true);
            if (prior is null)
            {
                attachments.Add(attachment);
                changes.Add(new VaultChange(attachment.Id, null, attachment.Path, true, true, false));
                continue;
            }

            var original = priorAttachments[prior.Id];
            var pathChanged = !string.Equals(original.Path, attachment.Path, StringComparison.Ordinal);
            var contentChanged = original.Length != attachment.Length ||
                !original.ContentSpan.SequenceEqual(attachment.ContentSpan);
            attachments.Add(pathChanged || contentChanged
                ? AttachmentRecord.Restore(original.Id, attachment.Path, attachment.ReadContent())
                : original);
            if (pathChanged || contentChanged)
            {
                changes.Add(new VaultChange(original.Id, original.Path,
                    attachment.Path, true, contentChanged, pathChanged));
            }
        }

        if (visited.Count != priorByPath.Count)
        {
            var missing = priorByPath.Values
                .Where(item => !visited.Contains(item))
                .OrderBy(item => item.Path, StringComparer.Ordinal).First();
            throw new InvalidOperationException(
                $"A prior vault file is missing: {missing.Path}. Restore it or supply an explicit rename mapping.");
        }

        var collection = new KnowledgeCollection(notes, attachments, previous.References);
        var addedLinks = 0;
        IReadOnlyList<AttachmentReference> removedLinkPairs = [];
        if (trackLinks || previous.VaultReferences is not null)
        {
            collection = LocalVaultAttachmentLinks.Track(collection);
            var oldLinks = previous.VaultReferences?.ToHashSet() ?? [];
            var newLinks = collection.VaultReferences!.ToHashSet();
            addedLinks = newLinks.Except(oldLinks).Count();
            removedLinkPairs = Array.AsReadOnly(oldLinks.Except(newLinks)
                .OrderBy(link => link.NoteId)
                .ThenBy(link => link.AttachmentId).ToArray());
            if (removedLinkPairs.Count != 0 && !previewLinkRemovals)
            {
                throw new InvalidOperationException(
                    $"{removedLinkPairs.Count} tracked attachment reference(s) disappeared. " +
                    "Review their exact IDs through PlanUpdate before approving removals.");
            }
        }

        return new VaultUpdate(collection, Array.AsReadOnly(changes.ToArray()),
            addedLinks, removedLinkPairs);

        PriorFile? Find(string path, bool isAttachment)
        {
            var prior = remapped.TryGetValue(path, out var mapped)
                ? mapped : priorByPath.GetValueOrDefault(path);
            if (prior is not null && (prior.IsAttachment != isAttachment || !visited.Add(prior)))
            {
                throw new InvalidDataException("A vault file changed type or has ambiguous identity.");
            }

            return prior;
        }
    }

    private static IReadOnlyList<VaultRename> ExpandMoves(
        IReadOnlyDictionary<string, PriorFile> previous, IReadOnlyList<VaultRename> rules)
    {
        if (rules.Count > 2_000)
        {
            throw new InvalidDataException("The ordered rename list exceeds its supported limit.");
        }

        var positions = previous.Values.ToDictionary(file => file, file => file.Path);
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                throw new ArgumentException("The rename list contains an empty mapping.", nameof(rules));
            }

            PortablePath.Validate(rule.FromPath);
            PortablePath.Validate(rule.ToPath);
            if (string.Equals(rule.FromPath, rule.ToPath, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The rename mapping is redundant.");
            }

            var affected = positions.Where(pair => rule.IsFolder
                    ? pair.Value.StartsWith(rule.FromPath + "/", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(pair.Value, rule.FromPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (affected.Length == 0 || !rule.IsFolder && affected.Length != 1)
            {
                throw new InvalidDataException("The rename mapping is missing or ambiguous.");
            }

            foreach (var (file, current) in affected)
            {
                var destination = rule.IsFolder
                    ? rule.ToPath + current[rule.FromPath.Length..] : rule.ToPath;
                PortablePath.Validate(destination);
                positions[file] = destination;
            }
        }

        return positions.Where(pair => !string.Equals(pair.Key.Path, pair.Value, StringComparison.Ordinal))
            .Select(pair => new VaultRename(pair.Key.Path, pair.Value))
            .ToArray();
    }

    private sealed record PriorFile(Guid Id, string Path, bool IsAttachment);
}
