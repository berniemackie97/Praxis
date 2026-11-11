using Praxis.Storage.Catalog;
using Praxis.Storage.Intake;
using Praxis.Storage.Processing;
using Praxis.Storage.Verification;
using Praxis.Worker.Desktop;

namespace Praxis.Worker.Intake;

internal static class IntakeCommand
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        bool desktopMode = args is [_, _, _, _, "--stdio"];
        if ((args.Length != 4 && !desktopMode) || args[0] != "--data-root" || args[2] != "--intake" || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[3]))
        {
            await Console.Error.WriteLineAsync("Usage: Praxis.Worker --data-root <absolute-private-data-directory> --intake <absolute-existing-inbox-directory> [--stdio]");
            return 2;
        }

        string dataRoot = Path.GetFullPath(args[1]);
        string inbox = Path.GetFullPath(args[3]);
        if (!Directory.Exists(inbox))
        {
            await Console.Error.WriteLineAsync("The approved intake path is offline; it will not be created.");
        }

        if (!Directory.Exists(dataRoot))
        {
            if (desktopMode)
            {
                throw new DirectoryNotFoundException("The configured private catalog is missing. Recovery requires owner action.");
            }

            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(dataRoot);
            }
            else
            {
                Directory.CreateDirectory(dataRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        LocalProcessingPipeline processing = LocalProcessingPipeline.CreateDefault(catalog, status: Report);
        LocalWatchedIntake monitor = new(catalog, inbox, status: Report, onCaptured: processing.Wake);
        LocalVerificationRunner verification = LocalVerificationRunner.CreateDefault(catalog, Report);
        if (desktopMode)
        {
            await DesktopStdioSession.RunAsync(catalog, monitor, processing, verification, inbox, cancellationToken);
        }
        else
        {
            await WorkerServices.RunAsync(cancellationToken, allowDesktopExit: false, monitor.RunAsync, processing.RunAsync, verification.RunAsync);
        }

        return 0;
    }

    private static void Report(string message) => Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] {message}");
}
