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

        Dictionary<string, PriorFile> priorByPath = new Dictionary<string, PriorFile>(StringComparer.OrdinalIgnoreCase);
        Dictionary<Guid, NoteRecord> priorNotes = previous.Notes.ToDictionary(note => note.Id);
        Dictionary<Guid, AttachmentRecord> priorAttachments = previous.Attachments.ToDictionary(attachment => attachment.Id);
        foreach (NoteRecord note in previous.Notes)
        {
            priorByPath.Add(note.Current.Path, new PriorFile(note.Id, note.Current.Path, false));
        }

        foreach (AttachmentRecord attachment in previous.Attachments)
        {
            priorByPath.Add(attachment.Path, new PriorFile(attachment.Id, attachment.Path, true));
        }

        HashSet<string> capturedPaths = captured.Notes.Select(note => note.Current.Path)
            .Concat(captured.Attachments.Select(attachment => attachment.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PriorFile> remapped = new Dictionary<string, PriorFile>(StringComparer.OrdinalIgnoreCase);
        HashSet<PriorFile> remappedSources = new HashSet<PriorFile>();
        IReadOnlyList<VaultRename> rules = renames ?? [];
        IReadOnlyList<VaultRename> mappings = rules.Any(rule => rule?.IsFolder == true)
            ? ExpandMoves(priorByPath, rules) : rules;
        foreach (VaultRename? rule in mappings)
        {
            if (rule is null)
            {
                throw new ArgumentException("The rename list contains an empty mapping.", nameof(renames));
            }

            PortablePath.Validate(rule.FromPath);
            PortablePath.Validate(rule.ToPath);
            if (string.Equals(rule.FromPath, rule.ToPath, StringComparison.Ordinal) ||
                !priorByPath.TryGetValue(rule.FromPath, out PriorFile? origin) ||
                !capturedPaths.Contains(rule.ToPath) ||
                !remappedSources.Add(origin) ||
                !remapped.TryAdd(rule.ToPath, origin))
            {
                throw new InvalidDataException("The rename mapping is redundant, missing or ambiguous.");
            }
        }

        HashSet<PriorFile> visited = new HashSet<PriorFile>();
        List<NoteRecord> notes = new List<NoteRecord>(captured.Notes.Count);
        List<AttachmentRecord> attachments = new List<AttachmentRecord>(captured.Attachments.Count);
        List<VaultChange> changes = new List<VaultChange>();

        foreach (NoteRecord note in captured.Notes)
        {
            string path = note.Current.Path;
            PriorFile? prior = Find(path, isAttachment: false);
            if (prior is null)
            {
                notes.Add(note);
                changes.Add(new VaultChange(note.Id, null, path, false, true, false));
                continue;
            }

            NoteRecord original = priorNotes[prior.Id];
            bool pathChanged = !string.Equals(original.Current.Path, path, StringComparison.Ordinal);
            bool bodyChanged = !string.Equals(original.Current.Markdown, note.Current.Markdown, StringComparison.Ordinal);
            notes.Add(pathChanged || bodyChanged
                ? original.Revise(path, note.Current.Markdown) : original);
            if (pathChanged || bodyChanged)
            {
                changes.Add(new VaultChange(original.Id, original.Current.Path,
                    path, false, bodyChanged, pathChanged));
            }
        }

        foreach (AttachmentRecord attachment in captured.Attachments)
        {
            PriorFile? prior = Find(attachment.Path, isAttachment: true);
            if (prior is null)
            {
                attachments.Add(attachment);
                changes.Add(new VaultChange(attachment.Id, null, attachment.Path, true, true, false));
                continue;
            }

            AttachmentRecord original = priorAttachments[prior.Id];
            bool pathChanged = !string.Equals(original.Path, attachment.Path, StringComparison.Ordinal);
            bool contentChanged = original.Length != attachment.Length ||
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
            PriorFile missing = priorByPath.Values
                .Where(item => !visited.Contains(item))
                .OrderBy(item => item.Path, StringComparer.Ordinal).First();
            throw new InvalidOperationException(
                $"A prior vault file is missing: {missing.Path}. Restore it or supply an explicit rename mapping.");
        }

        KnowledgeCollection collection = new KnowledgeCollection(notes, attachments, previous.References);
        int addedLinks = 0;
        IReadOnlyList<AttachmentReference> removedLinkPairs = [];
        if (trackLinks || previous.VaultReferences is not null)
        {
            collection = LocalVaultAttachmentLinks.Track(collection);
            HashSet<AttachmentReference> oldLinks = previous.VaultReferences?.ToHashSet() ?? [];
            HashSet<AttachmentReference> newLinks = collection.VaultReferences!.ToHashSet();
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
            PriorFile? prior = remapped.TryGetValue(path, out PriorFile? mapped)
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

        Dictionary<PriorFile, string> positions = previous.Values.ToDictionary(file => file, file => file.Path);
        foreach (VaultRename? rule in rules)
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

            KeyValuePair<PriorFile, string>[] affected = positions.Where(pair => rule.IsFolder
                    ? pair.Value.StartsWith(rule.FromPath + "/", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(pair.Value, rule.FromPath, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (affected.Length == 0 || !rule.IsFolder && affected.Length != 1)
            {
                throw new InvalidDataException("The rename mapping is missing or ambiguous.");
            }

            foreach ((PriorFile file, string current) in affected)
            {
                string destination = rule.IsFolder
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
