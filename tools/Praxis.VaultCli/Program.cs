using Praxis.Knowledge;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length == 2 && args[0] == "verify")
{
    return Run(() =>
    {
        using var stream = File.OpenRead(args[1]);
        var collection = NoteArchive.ReadCollection(stream);
        Console.WriteLine($"Verified {collection.Notes.Count} notes and {collection.Attachments.Count} attachments.");
    });
}

if (args.Length == 3 && args[0] == "capture")
{
    return Run(() =>
    {
        LocalVaultExchange.CaptureArchive(args[1], args[2]);
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
        var update = LocalVaultExchange.PlanUpdate(args[1], args[2], ReadRenames(args, 3));
        PrintChanges(update);
    });
}

if (args.Length >= 4 && args[0] == "update")
{
    return Run(() =>
    {
        var update = LocalVaultExchange.UpdateArchive(args[1], args[2], args[3],
            ReadRenames(args, 4));
        PrintChanges(update);
        Console.WriteLine("Updated archive created.");
    });
}

Console.Error.WriteLine("Usage: vault capture <source-directory> <new-archive.zip>");
Console.Error.WriteLine("       vault verify  <archive.zip>");
Console.Error.WriteLine("       vault restore <archive.zip> <new-directory>");
Console.Error.WriteLine("       vault plan <source-directory> <previous-archive.zip> [--renames <json-file>]");
Console.Error.WriteLine("       vault update <source-directory> <previous-archive.zip> <new-archive.zip> [--renames <json-file>]");
return 2;

static int Run(Action action)
{
    try
    {
        action();
        return 0;
    }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or IOException or
        UnauthorizedAccessException or NotSupportedException)
    {
        Console.Error.WriteLine($"Vault operation failed: {error.Message}");
        return 1;
    }
}

static IReadOnlyList<VaultRename> ReadRenames(string[] arguments, int baseLength)
{
    if (arguments.Length == baseLength)
    {
        return [];
    }

    if (arguments.Length != baseLength + 2 || arguments[baseLength] != "--renames")
    {
        throw new ArgumentException("Expected an optional --renames <json-file> mapping.");
    }

    var filename = arguments[baseLength + 1];
    var info = new FileInfo(filename);
    if (info.Length > 256 * 1024)
    {
        throw new InvalidDataException("The rename mapping exceeds its size limit.");
    }

    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    var mappings = JsonSerializer.Deserialize<VaultRename[]>(File.ReadAllBytes(filename), options)
        ?? throw new InvalidDataException("The rename mapping is empty.");
    if (mappings.Length > 20_000)
    {
        throw new InvalidDataException("The rename mapping contains too many entries.");
    }

    return Array.AsReadOnly(mappings);
}

static void PrintChanges(VaultUpdate update)
{
    var added = update.Changes.Count(change => change.PreviousPath is null);
    var updated = update.Changes.Count(change => change.ContentChanged && change.PreviousPath is not null);
    var moved = update.Changes.Count(change => change.PathChanged);
    Console.WriteLine($"Changes: {added} added, {updated} updated, {moved} renamed.");
    Console.WriteLine($"Collection: {update.Collection.Notes.Count} notes, {update.Collection.Attachments.Count} attachments.");
}
