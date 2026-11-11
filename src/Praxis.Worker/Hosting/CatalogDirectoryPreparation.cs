namespace Praxis.Worker.Hosting;

/// <summary>
/// Ensures the private catalog directory exists before the worker opens it. A desktop session
/// never initializes a missing catalog: recovery of a lost store is an owner decision.
/// </summary>
internal static class CatalogDirectoryPreparation
{
    internal static void Prepare(WorkerOptions options)
    {
        if (options.IntakeRoots.Any(root => !Directory.Exists(root)))
        {
            Console.Error.WriteLine("An approved intake path is offline; it will not be created.");
        }

        if (Directory.Exists(options.DataRoot))
        {
            return;
        }

        if (options.DesktopMode)
        {
            throw new DirectoryNotFoundException("The configured private catalog is missing. Recovery requires owner action.");
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(options.DataRoot);
        }
        else
        {
            Directory.CreateDirectory(options.DataRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
