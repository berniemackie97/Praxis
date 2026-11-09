using System.Text;
using Praxis.Storage;

namespace Praxis.Worker;

internal static class DesktopStdioSession
{
    internal static async Task RunAsync(
        LocalCatalog catalog, LocalWatchedIntake monitor, LocalProcessingPipeline processing,
        string inbox, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Console.OpenStandardInput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false);
        using var writer = new StreamWriter(Console.OpenStandardOutput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        var frames = new DesktopRequestReader(reader);
        var protocol = new LocalDesktopProtocol(
            catalog, new LocalQuickCapture(catalog, inbox), monitor, processing);

        async Task ServeAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && !protocol.StopRequested)
            {
                var request = await frames.ReadAsync(token);
                if (request is null)
                {
                    return;
                }

                var response = await protocol.HandleAsync(request, token);
                await writer.WriteLineAsync(response.AsMemory(), token);
            }
        }

        await WorkerServices.RunAsync(cancellationToken, ServeAsync, monitor.RunAsync, processing.RunAsync);
    }
}
