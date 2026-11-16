using Praxis.Application.Agents.Capabilities;
using Praxis.Infrastructure.Inference.Subscription;

namespace Praxis.Infrastructure.Agents.Capabilities;

/// <summary>Runs one literal package manager command whose target was stored before owner approval.</summary>
internal static class PackageCapabilityInstaller
{
    public static async Task<CapabilityExecution> InstallAsync(CapabilityRequest request, string workspace,
        Dictionary<string, string> environment, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new CapabilityExecution(false, "Package installation from Chat currently requires macOS.");
        }

        string manager = request.Operation == CapabilityService.BrewInstall ? "brew" : "npm";
        if (InstalledProgramProbe.Find(manager) is not { } executable)
        {
            return new CapabilityExecution(false, $"{manager} is unavailable in the standard installation locations.");
        }

        environment["PATH"] = InstalledProgramProbe.SearchPath;
        environment["HOMEBREW_NO_AUTO_UPDATE"] = "1";
        environment["HOMEBREW_NO_ANALYTICS"] = "1";
        environment["NPM_CONFIG_USERCONFIG"] = "/dev/null";
        environment["NPM_CONFIG_GLOBALCONFIG"] = "/dev/null";
        environment["NPM_CONFIG_CACHE"] = Path.Combine(workspace, "npm-cache");

        // Never interpolate a shell string or use an npm lifecycle script, URL, git spec, local file, or package tag.
        string[] args = request.Operation == CapabilityService.BrewInstall
            ? ["install", "--", request.Target!]
            : ["install", "--global", "--ignore-scripts", "--no-audit", "--no-fund", "--registry=https://registry.npmjs.org", "--", request.Target!];
        CliResult result = await CliProcess.RunAsync(executable, args, null, workspace, TimeSpan.FromMinutes(3), cancellationToken, environment);
        return LocalCapabilityExecutor.Result(result, "The package manager reported a successful installation.");
    }
}
