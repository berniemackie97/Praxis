using System.Text;
using Praxis.Storage;

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
        Console.Error.WriteLine("The configured private catalog is missing. Recovery requires owner action.");
        return 1;
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

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    using var catalog = LocalCatalog.Open(dataRoot);
    var monitor = new LocalWatchedIntake(
        catalog, inbox, status: message =>
            Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] {message}"));
    if (!desktopMode)
    {
        await monitor.RunAsync(cancellation.Token);
        return 0;
    }

    var watching = monitor.RunAsync(cancellation.Token);
    try
    {
        var protocol = new LocalDesktopProtocol(catalog, new LocalQuickCapture(catalog, inbox), monitor);
        using var reader = new StreamReader(Console.OpenStandardInput(),
            new UTF8Encoding(false, true));
        using var writer = new StreamWriter(Console.OpenStandardOutput(),
            new UTF8Encoding(false)) { AutoFlush = true };
        while (!cancellation.IsCancellationRequested && !protocol.StopRequested)
        {
            var line = await ReadBoundedRequestAsync(reader, cancellation.Token);
            if (line is null)
            {
                break;
            }

            await writer.WriteLineAsync(
                await protocol.HandleAsync(line, cancellation.Token));
        }
    }
    finally
    {
        await cancellation.CancelAsync();
        await watching;
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
    Console.Error.WriteLine("Praxis intake cannot start: " + exception.Message);
    return 1;
}


static async Task<string?> ReadBoundedRequestAsync(
    TextReader reader, CancellationToken cancellationToken)
{
    const int maximumCharacters = 16_384;
    var content = new StringBuilder();
    var character = new char[1];
    while (await reader.ReadAsync(character.AsMemory(), cancellationToken) != 0)
    {
        if (character[0] == '\n')
        {
            return content.ToString().TrimEnd('\r');
        }

        if (content.Length == maximumCharacters)
        {
            throw new InvalidDataException("The desktop request exceeds its size limit.");
        }

        content.Append(character[0]);
    }

    return content.Length == 0 ? null : content.ToString();
}
