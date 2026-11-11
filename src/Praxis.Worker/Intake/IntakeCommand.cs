using Praxis.Application.Processing;
using Praxis.Application.Verification;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Inference.Subscription;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Persistence;
using Praxis.Worker.Desktop;

namespace Praxis.Worker.Intake;

internal static class IntakeCommand
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        bool desktopMode = args.Length > 0 && args[^1] == "--stdio";
        int argumentCount = args.Length - (desktopMode ? 1 : 0);
        if (argumentCount < 4 || argumentCount % 2 != 0 ||
            args[0] != "--data-root" || !Path.IsPathFullyQualified(args[1]) ||
            args[2] != "--intake" || (argumentCount - 2) / 2 > LocalWatchedIntakeGroup.MaximumRoots)
        {
            await Console.Error.WriteLineAsync("Usage: Praxis.Worker --data-root <absolute-private-data-directory> --intake <absolute-inbox> [--intake <another-approved-folder> ...] [--stdio]");
            return 2;
        }

        List<string> roots = new();
        for (int index = 2; index < argumentCount; index += 2)
        {
            if (args[index] != "--intake" || !Path.IsPathFullyQualified(args[index + 1]))
            {
                await Console.Error.WriteLineAsync("Each watched folder must be supplied as --intake <absolute-path>.");
                return 2;
            }

            roots.Add(Path.GetFullPath(args[index + 1]));
        }

        string dataRoot = Path.GetFullPath(args[1]);
        if (roots.Any(root => !Directory.Exists(root)))
        {
            await Console.Error.WriteLineAsync("An approved intake path is offline; it will not be created.");
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

        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        ProcessingPipeline processing = CatalogProcessing.CreatePipeline(catalog, status: Report);
        LocalWatchedIntakeGroup monitors = new(catalog, roots, status: Report, onCaptured: processing.Wake);
        VerificationRunner verification = new(catalog.Verification, new SubscriptionVerifierModelFactory(), TimeProvider.System, status: Report);
        if (desktopMode)
        {
            await DesktopStdioSession.RunAsync(catalog, monitors, processing, verification, roots[0], cancellationToken);
        }
        else
        {
            await WorkerServices.RunAsync(cancellationToken, allowDesktopExit: false,
                monitors.RunAsync, processing.RunAsync, verification.RunAsync);
        }

        return 0;
    }

    private static void Report(string message) => Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] {message}");
}
