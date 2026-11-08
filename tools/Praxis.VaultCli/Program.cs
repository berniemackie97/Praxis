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
        var options = ReadOptions(args, 3, allowRenames: false, allowRemovals: false);
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
        var options = ReadOptions(args, 3);
        var update = LocalVaultExchange.PlanUpdate(args[1], args[2], options.Renames,
            options.TrackLinks, options.AcceptLinkRemovals);
        PrintChanges(update);
    });
}

if (args.Length >= 4 && args[0] == "update")
{
    return Run(() =>
    {
        var options = ReadOptions(args, 4);
        var update = LocalVaultExchange.UpdateArchive(args[1], args[2], args[3],
            options.Renames, options.TrackLinks, options.AcceptLinkRemovals);
        PrintChanges(update);
        Console.WriteLine("Updated archive created.");
    });
}

Console.Error.WriteLine("Usage: vault capture <source-directory> <new-archive.zip> [--track-links]");
Console.Error.WriteLine("       vault verify  <archive.zip>");
Console.Error.WriteLine("       vault restore <archive.zip> <new-directory>");
Console.Error.WriteLine("       vault links <archive.zip>");
Console.Error.WriteLine("       vault plan <source-directory> <previous-archive.zip> [--renames <json-file>] [--track-links] [--accept-link-removals]");
Console.Error.WriteLine("       vault update <source-directory> <previous-archive.zip> <new-archive.zip> [--renames <json-file>] [--track-links] [--accept-link-removals]");
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

static (IReadOnlyList<VaultRename> Renames, bool TrackLinks, bool AcceptLinkRemovals)
    ReadOptions(string[] arguments, int baseLength, bool allowRenames = true, bool allowRemovals = true)
{
    string? renamePath = null;
    var trackLinks = false;
    var acceptLinkRemovals = false;
    for (var index = baseLength; index < arguments.Length; index++)
    {
        switch (arguments[index])
        {
            case "--track-links" when !trackLinks:
                trackLinks = true;
                break;
            case "--accept-link-removals" when allowRemovals && !acceptLinkRemovals:
                acceptLinkRemovals = true;
                break;
            case "--renames" when allowRenames && renamePath is null && index + 1 < arguments.Length:
                renamePath = arguments[++index];
                break;
            default:
                throw new ArgumentException("Invalid or duplicate vault option.");
        }
    }

    if (renamePath is null)
    {
        return ([], trackLinks, acceptLinkRemovals);
    }

    var info = new FileInfo(renamePath);
    if (info.Length > 256 * 1024)
    {
        throw new InvalidDataException("The rename mapping exceeds its size limit.");
    }

    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    var mappings = JsonSerializer.Deserialize<VaultRename[]>(File.ReadAllBytes(renamePath), options)
        ?? throw new InvalidDataException("The rename mapping is empty.");
    if (mappings.Length > 20_000)
    {
        throw new InvalidDataException("The rename mapping contains too many entries.");
    }

    return (Array.AsReadOnly(mappings), trackLinks, acceptLinkRemovals);
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
    }
}
