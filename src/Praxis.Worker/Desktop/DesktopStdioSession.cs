using System.Text;
using Praxis.Storage;

namespace Praxis.Worker;

internal static class DesktopStdioSession
{
    internal static async Task RunAsync(
        LocalCatalog catalog, LocalWatchedIntake monitor, string inbox,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Console.OpenStandardInput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false);
        using var writer = new StreamWriter(Console.OpenStandardOutput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var frames = new DesktopRequestReader(reader);
        var protocol = new LocalDesktopProtocol(catalog, new LocalQuickCapture(catalog, inbox), monitor);
        var watching = monitor.RunAsync(lifetime.Token);
        try
        {
            while (!lifetime.IsCancellationRequested && !protocol.StopRequested)
            {
                var request = await frames.ReadAsync(lifetime.Token);
                if (request is null)
                {
                    break;
                }

                var response = await protocol.HandleAsync(request, lifetime.Token);
                await writer.WriteLineAsync(response.AsMemory(), lifetime.Token);
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await watching;
        }
    }
}
