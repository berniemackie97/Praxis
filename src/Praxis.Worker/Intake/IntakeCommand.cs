using Praxis.Storage;

namespace Praxis.Worker;

internal static class IntakeCommand
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var desktopMode = args.Length == 5 && args[4] == "--stdio";
        if ((args.Length != 4 && !desktopMode) ||
            args[0] != "--data-root" || args[2] != "--intake" ||
            !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[3]))
        {
            Console.Error.WriteLine(
                "Usage: Praxis.Worker --data-root <absolute-private-data-directory> --intake <absolute-existing-inbox-directory> [--stdio]");
            return 2;
        }

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
        var processing = LocalProcessingPipeline.CreateDefault(catalog, status: Report);
        var monitor = new LocalWatchedIntake(catalog, inbox,
            status: Report, onCaptured: processing.Wake);
        if (desktopMode)
        {
            await DesktopStdioSession.RunAsync(catalog, monitor, processing, inbox, cancellationToken);
        }
        else
        {
            await WorkerServices.RunAsync(cancellationToken, monitor.RunAsync, processing.RunAsync);
        }

        return 0;
    }

    private static void Report(string message) =>
        Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] {message}");
}
