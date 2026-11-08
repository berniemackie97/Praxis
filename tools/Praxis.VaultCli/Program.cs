using Praxis.Knowledge;

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

Console.Error.WriteLine("Usage: vault capture <source-directory> <new-archive.zip>");
Console.Error.WriteLine("       vault verify  <archive.zip>");
Console.Error.WriteLine("       vault restore <archive.zip> <new-directory>");
return 2;

static int Run(Action action)
{
    try
    {
        action();
        return 0;
    }
    catch (Exception error) when (error is ArgumentException or InvalidDataException or IOException or
        UnauthorizedAccessException or NotSupportedException)
    {
        Console.Error.WriteLine($"Vault operation failed: {error.Message}");
        return 1;
    }
}
