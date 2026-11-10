using System.Text;
using Praxis.Storage.Catalog;
using Praxis.Storage.Desktop;
using Praxis.Storage.Intake;
using Praxis.Storage.Processing;

namespace Praxis.Worker.Desktop;

internal static class DesktopStdioSession
{
    internal static async Task RunAsync(LocalCatalog catalog, LocalWatchedIntake monitor, LocalProcessingPipeline processing, string inbox, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false);
        using StreamWriter writer = new(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        DesktopRequestReader frames = new(reader);
        LocalDesktopProtocol protocol = new(catalog, new LocalQuickCapture(catalog, inbox), monitor, processing);

        async Task ServeAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && !protocol.StopRequested)
            {
                string? request = await frames.ReadAsync(token);
                if (request is null)
                {
                    return;
                }

                string response = await protocol.HandleAsync(request, token);
                await writer.WriteLineAsync(response.AsMemory(), token);
            }
        }

        await WorkerServices.RunAsync(cancellationToken, ServeAsync, monitor.RunAsync, processing.RunAsync);
    }
}
