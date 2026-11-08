using System.Text.Json;
using System.Text.Json.Serialization;
using Praxis.Knowledge;

if (args.Length == 2 && args[0] == "verify")
{
    return Run(() =>
    {
        using var stream = File.OpenRead(args[1]);
        var collection = NoteArchive.ReadCollection(stream);
        Console.WriteLine($"Verified {collection.Notes.Count} notes and {collection.Attachments.Count} attachments.");
        if (collection.VaultReferences is not null)
        {
            Console.WriteLine($"Tracked {collection.VaultReferences.Count} vault attachment references.");
        }
    });
}

if (args.Length == 2 && args[0] == "links")
{
    return Run(() =>
    {
        using var stream = File.OpenRead(args[1]);
        var report = LocalVaultAttachmentLinks.Inspect(NoteArchive.ReadCollection(stream));
        Console.WriteLine($"Resolved {report.References.Count} note-to-attachment links; {report.Problems.Count} unresolved or ambiguous.");
        foreach (var problem in report.Problems)
        {
            Console.Error.WriteLine($"{problem.NotePath}: {problem.Target}: {problem.Reason}");
        }

        if (report.Problems.Count != 0)
        {
            throw new InvalidDataException("The attachment link audit has unresolved findings.");
        }
    });
}

if (args.Length >= 3 && args[0] == "capture")
{
    return Run(() =>
    {
        var options = ReadOptions(args, 3, allowRenames: false, allowUnlinks: false);
        LocalVaultExchange.CaptureArchive(args[1], args[2], options.TrackLinks);
        Console.WriteLine("Vault archive created.");
    });
}

if (args.Length == 3 && args[0] == "restore")
{
    return Run(() =>
    {
        LocalVaultExchange.RestoreArchive(args[1], args[2]);
        Console.WriteLine("Vault restored to a new directory.");
    });
}

if (args.Length >= 3 && args[0] == "plan")
{
    return Run(() =>
    {
        var options = ReadOptions(args, 3, allowUnlinks: false);
        var update = LocalVaultExchange.PlanUpdate(args[1], args[2], options.Renames,
            options.TrackLinks);
        PrintChanges(update);
    });
}

if (args.Length >= 4 && args[0] == "update")
{
    return Run(() =>
    {
        var options = ReadOptions(args, 4);
        var update = LocalVaultExchange.UpdateArchive(args[1], args[2], args[3],
            options.Renames, options.TrackLinks, options.ApprovedUnlinks);
        PrintChanges(update);
        Console.WriteLine("Updated archive created.");
    });
}

Console.Error.WriteLine("Usage: vault capture <source-directory> <new-archive.zip> [--track-links]");
Console.Error.WriteLine("       vault verify  <archive.zip>");
Console.Error.WriteLine("       vault restore <archive.zip> <new-directory>");
Console.Error.WriteLine("       vault links <archive.zip>");
Console.Error.WriteLine("       vault plan <source-directory> <previous-archive.zip> [--renames <json-file>] [--track-links]");
Console.Error.WriteLine("       vault update <source-directory> <previous-archive.zip> <new-archive.zip> [--renames <json-file>] [--track-links] [--approve-unlinks <json-file>]");
return 2;

static int Run(Action action)
{
    try
    {
        action();
        return 0;
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or JsonException or IOException or
        UnauthorizedAccessException or NotSupportedException)
    {
        Console.Error.WriteLine($"Vault operation failed: {error.Message}");
        return 1;
    }
}

static (IReadOnlyList<VaultRename> Renames, bool TrackLinks,
    IReadOnlyList<AttachmentReference>? ApprovedUnlinks)
    ReadOptions(string[] arguments, int baseLength, bool allowRenames = true, bool allowUnlinks = true)
{
    string? renamePath = null;
    var trackLinks = false;
    string? unlinkPath = null;
    for (var index = baseLength; index < arguments.Length; index++)
    {
        switch (arguments[index])
        {
            case "--track-links" when !trackLinks:
                trackLinks = true;
                break;
            case "--approve-unlinks" when allowUnlinks && unlinkPath is null && index + 1 < arguments.Length:
                unlinkPath = arguments[++index];
                break;
            case "--renames" when allowRenames && renamePath is null && index + 1 < arguments.Length:
                renamePath = arguments[++index];
                break;
            default:
                throw new ArgumentException("Invalid or duplicate vault option.");
        }
    }

    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    IReadOnlyList<VaultRename> mappings = [];
    if (renamePath is not null)
    {
        if (new FileInfo(renamePath).Length > 256 * 1024)
        {
            throw new InvalidDataException("The rename mapping exceeds its size limit.");
        }

        var renames = JsonSerializer.Deserialize<VaultRename[]>(File.ReadAllBytes(renamePath), options)
            ?? throw new InvalidDataException("The rename mapping is empty.");
        if (renames.Length > 20_000)
        {
            throw new InvalidDataException("The rename mapping contains too many entries.");
        }

        mappings = Array.AsReadOnly(renames);
    }

    IReadOnlyList<AttachmentReference>? approved = null;
    if (unlinkPath is not null)
    {
        if (new FileInfo(unlinkPath).Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("The link removal approval exceeds its size limit.");
        }

        var pairs = JsonSerializer.Deserialize<AttachmentReference[]>(File.ReadAllBytes(unlinkPath), options)
            ?? throw new InvalidDataException("The link removal approval is empty.");
        if (pairs.Length > 50_000 || pairs.Any(pair => pair is null ||
                pair.NoteId == Guid.Empty || pair.AttachmentId == Guid.Empty))
        {
            throw new InvalidDataException("The link removal approval contains invalid pairs.");
        }

        approved = Array.AsReadOnly(pairs);
    }

    return (mappings, trackLinks, approved);
}

static void PrintChanges(VaultUpdate update)
{
    var added = update.Changes.Count(change => change.PreviousPath is null);
    var updated = update.Changes.Count(change => change.ContentChanged && change.PreviousPath is not null);
    var moved = update.Changes.Count(change => change.PathChanged);
    Console.WriteLine($"Changes: {added} added, {updated} updated, {moved} renamed.");
    Console.WriteLine($"Collection: {update.Collection.Notes.Count} notes, {update.Collection.Attachments.Count} attachments.");
    if (update.Collection.VaultReferences is not null)
    {
        Console.WriteLine($"Vault links: {update.Collection.VaultReferences.Count} tracked, " +
            $"{update.AddedLinks} added, {update.RemovedLinks} removed.");
        if (update.RemovedLinks != 0)
        {
            var notes = update.Collection.Notes.ToDictionary(note => note.Id);
            var attachments = update.Collection.Attachments.ToDictionary(attachment => attachment.Id);
            foreach (var pair in update.RemovedLinkPairs!)
            {
                Console.WriteLine($"Removed vault link: noteId={pair.NoteId} ({notes[pair.NoteId].Current.Path}), " +
                    $"attachmentId={pair.AttachmentId} ({attachments[pair.AttachmentId].Path})");
            }
        }
    }
}
