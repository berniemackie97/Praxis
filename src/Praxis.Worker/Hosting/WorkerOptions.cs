using Praxis.Infrastructure.Intake;

namespace Praxis.Worker.Hosting;

/// <summary>The catalog and approved folders a serving worker owns, parsed and validated from its arguments.</summary>
internal sealed record WorkerOptions(string DataRoot, IReadOnlyList<string> IntakeRoots, IReadOnlySet<string> PendingIntakeRoots, bool DesktopMode)
{
    internal const string Usage =
        "Usage: Praxis.Worker --data-root <absolute-private-data-directory> --intake <absolute-inbox> [--intake <another-approved-folder> ...] [--stdio]";

    /// <summary>The folder that receives quick notes.</summary>
    internal string PrimaryIntakeRoot => IntakeRoots[0];

    internal static bool TryParse(IReadOnlyList<string> args, out WorkerOptions? options, out string? error)
    {
        options = null;
        bool desktopMode = args.Count > 0 && args[^1] == "--stdio";
        int argumentCount = args.Count - (desktopMode ? 1 : 0);
        if (argumentCount < 4 || argumentCount % 2 != 0 ||
            args[0] != "--data-root" || !Path.IsPathFullyQualified(args[1]) ||
            (argumentCount - 2) / 2 > WatchedFolderIntakeGroup.MaximumRoots)
        {
            error = Usage;
            return false;
        }

        List<string> roots = new();
        HashSet<string> pending = new(StringComparer.Ordinal);
        for (int index = 2; index < argumentCount; index += 2)
        {
            bool needsApproval = desktopMode && args[index] == "--intake-pending";
            if ((args[index] != "--intake" && !needsApproval) || !Path.IsPathFullyQualified(args[index + 1]))
            {
                error = "Each watched folder must be supplied as --intake <absolute-path>.";
                return false;
            }

            string path = Path.GetFullPath(args[index + 1]);
            roots.Add(path);
            if (needsApproval)
            {
                pending.Add(path);
            }
        }

        options = new WorkerOptions(Path.GetFullPath(args[1]), roots, pending, desktopMode);
        error = null;
        return true;
    }
}
