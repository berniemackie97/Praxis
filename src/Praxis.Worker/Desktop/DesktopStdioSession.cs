using System.Text;
using Praxis.Application.Desktop;

namespace Praxis.Worker.Desktop;

/// <summary>Serves the desktop protocol over the parent process's stdin and stdout pipes until EOF or a stop request.</summary>
internal sealed class DesktopStdioSession(DesktopProtocol protocol)
{
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        using StreamReader reader = new(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false);
        using StreamWriter writer = new(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        DesktopRequestReader frames = new(reader);
        while (!cancellationToken.IsCancellationRequested && !protocol.StopRequested)
        {
            string? request = await frames.ReadAsync(cancellationToken);
            if (request is null)
            {
                return;
            }

            string response = await protocol.HandleAsync(request, cancellationToken);
            await writer.WriteLineAsync(response.AsMemory(), cancellationToken);
        }
    }
}
