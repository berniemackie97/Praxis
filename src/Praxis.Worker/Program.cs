using Praxis.Storage;
using Praxis.Worker;

var desktopMode = args.Length == 5 && args[4] == "--stdio";
if ((args.Length != 4 && !desktopMode) ||
    args[0] != "--data-root" || args[2] != "--intake" ||
    !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[3]))
{
    Console.Error.WriteLine(
        "Usage: Praxis.Worker --data-root <absolute-private-data-directory> --intake <absolute-existing-inbox-directory> [--stdio]");
    return 2;
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler stop = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += stop;

try
{
    var dataRoot = Path.GetFullPath(args[1]);
    var inbox = Path.GetFullPath(args[3]);
    if (!Directory.Exists(inbox))
    {
        Console.Error.WriteLine("The approved intake path is offline; it will not be created.");
    }

    if (!Directory.Exists(dataRoot))
    {
        if (desktopMode)
        {
            throw new DirectoryNotFoundException(
                "The configured private catalog is missing. Recovery requires owner action.");
        }

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

    using var catalog = LocalCatalog.Open(dataRoot);
    var monitor = new LocalWatchedIntake(catalog, inbox, status: message =>
        Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] {message}"));
    if (desktopMode)
    {
        await DesktopStdioSession.RunAsync(catalog, monitor, inbox, cancellation.Token);
    }
    else
    {
        await monitor.RunAsync(cancellation.Token);
    }

    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
    or InvalidDataException or InvalidOperationException or ArgumentException
    or NotSupportedException)
{
    Console.Error.WriteLine("Praxis worker failed: " + exception.Message);
    return 1;
}
finally
{
    Console.CancelKeyPress -= stop;
}
