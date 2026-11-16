using Praxis.Application.Agents.Capabilities;
using Praxis.Infrastructure.Inference.Subscription;

namespace Praxis.Infrastructure.Agents.Capabilities;

/// <summary>Runs only concrete actions that the owner approved through the private desktop UI.</summary>
public sealed class LocalCapabilityExecutor : ICapabilityExecutor
{
    private static readonly TimeSpan ActionDeadline = TimeSpan.FromSeconds(20);

    public async Task<CapabilityExecution> ExecuteAsync(CapabilityRequest request, CancellationToken cancellationToken)
    {
        using CliWorkspace workspace = new(OperatingSystem.IsMacOS() ? "/private/tmp" : null);
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin:/bin",
            ["HOME"] = workspace.Path,
            ["TMPDIR"] = workspace.Path,
            ["LANG"] = "C"
        };

        switch (request.Operation)
        {
            case CapabilityService.BrewInstall or CapabilityService.NpmInstall:
                return await PackageCapabilityInstaller.InstallAsync(request, workspace.Path, environment, cancellationToken);
            case CapabilityService.BrowserOpen when OperatingSystem.IsMacOS():
                return await RunAsync("/usr/bin/open", [request.Target!], workspace.Path, environment,
                    "Opened the approved address in your default browser.", cancellationToken);
            case CapabilityService.Terminal when OperatingSystem.IsMacOS() && File.Exists("/usr/bin/sandbox-exec"):
                environment["PATH"] = InstalledProgramProbe.SearchPath;
                environment["OPENSSL_CONF"] = "/dev/null";
                return await RunAsync("/usr/bin/sandbox-exec",
                    ["-p", TerminalProfile(workspace.Path), "/bin/sh", "-c", "ulimit -t 8 -f 8192 -n 64; exec /bin/sh -c \"$1\"", "praxis", request.Target!],
                    workspace.Path, environment, "The command completed without output.", cancellationToken);
            default:
                return new CapabilityExecution(false, "This action is not supported on this host.");
        }
    }

    private static async Task<CapabilityExecution> RunAsync(string executable, IReadOnlyList<string> args, string path,
        IReadOnlyDictionary<string, string> environment, string success, CancellationToken cancellationToken)
    {
        CliResult result = await CliProcess.RunAsync(executable, args, null, path, ActionDeadline, cancellationToken, environment);
        return Result(result, success);
    }

    internal static CapabilityExecution Result(CliResult result, string success)
    {
        string output = (result.Stdout.Trim() + "\n" + result.Stderr.Trim()).Trim();
        string detail = output.Length == 0 ? success : output[..Math.Min(5_800, output.Length)];
        return result.ExitCode == 0 ? new CapabilityExecution(true, detail) :
            new CapabilityExecution(false, $"Exit code {result.ExitCode}.\n{output[..Math.Min(5_800, output.Length)]}");
    }

    // Metadata reads are needed by dyld, but data and xattrs are restricted to system binaries and
    // this isolated workspace. No network, sandbox escape service, or inherited owner environment.
    private static string TerminalProfile(string root) => $$"""
        (version 1)
        (deny default)
        (allow process-exec process-fork)
        (allow signal (target self))
        (allow sysctl-read (sysctl-name-regex #"^hw\.") (sysctl-name-regex #"^kern\.(os|host|version)"))
        (allow file-read*)
        (deny file-read-data file-read-xattr
          (require-not (require-any
            (subpath "/System") (subpath "/usr/lib") (subpath "/usr/bin") (subpath "/usr/share") (subpath "/bin")
            (subpath "/opt/homebrew/bin") (subpath "/opt/homebrew/Cellar") (subpath "/opt/homebrew/opt") (subpath "/opt/homebrew/lib")
            (subpath "/usr/local/bin") (subpath "/usr/local/lib") (subpath "/usr/local/Cellar") (subpath "/usr/local/opt")
            (literal "/") (literal "/private") (literal "/private/tmp") (literal "/dev/null") (literal "/dev/urandom")
            (subpath "{{root}}"))))
        (allow file-write* (subpath "{{root}}") (literal "/dev/null"))
        """;
}
