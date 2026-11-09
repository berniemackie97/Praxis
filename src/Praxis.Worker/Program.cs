using Praxis.Storage;

if (args.Length != 4 || args[0] != "--data-root" || args[2] != "--intake" ||
    !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[3]))
{
    Console.Error.WriteLine(
        "Usage: Praxis.Worker --data-root <absolute-private-data-directory> --intake <absolute-existing-inbox-directory>");
    return 2;
}

var dataRoot = Path.GetFullPath(args[1]);
var inbox = Path.GetFullPath(args[3]);
if (!Directory.Exists(inbox))
{
    Console.WriteLine("The approved intake path is currently offline; it will not be created.");
}

if (!Directory.Exists(dataRoot))
{
    if (OperatingSystem.IsWindows())
    {
        Directory.CreateDirectory(dataRoot);
    }
    else
    {
        Directory.CreateDirectory(dataRoot,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    using var catalog = LocalCatalog.Open(dataRoot);
    var monitor = new LocalWatchedIntake(
        catalog, inbox, status: message =>
            Console.WriteLine($"[{DateTimeOffset.Now:O}] {message}"));
    await monitor.RunAsync(cancellation.Token);
    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
    or InvalidDataException or InvalidOperationException or ArgumentException
    or NotSupportedException)
{
    Console.Error.WriteLine("Praxis intake cannot start: " + exception.Message);
    return 1;
}
