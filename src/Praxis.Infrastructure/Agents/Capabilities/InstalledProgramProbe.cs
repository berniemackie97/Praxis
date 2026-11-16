using Praxis.Application.Agents.Capabilities;

namespace Praxis.Infrastructure.Agents.Capabilities;

internal sealed class InstalledProgramProbe : IInstalledProgramProbe
{
    internal const string SearchPath = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin";

    public bool IsAvailable(string program) => Find(program) is not null;

    internal static string? Find(string program)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (string directory in SearchPath.Split(':'))
        {
            string path = Path.Combine(directory, program);
            if (File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                return path;
            }
        }

        return null;
    }
}
