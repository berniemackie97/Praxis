namespace Praxis.Inference;

/// <summary>
/// An empty, owner-private working directory for one provider call, so the CLI loads no
/// project instructions, settings, hooks or MCP servers from wherever Praxis happens to run.
/// </summary>
internal sealed class CliWorkspace : IDisposable
{
    internal CliWorkspace()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "praxis-provider-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Path = path;
    }

    internal string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A provider that left a file open must not turn a finished call into a failure;
            // the uniquely named directory lives under the user's private temp folder.
        }
    }
}
