using System.Text;
using Praxis.Application.Processing;
using Praxis.Application.Verification;
using Praxis.Infrastructure.Desktop;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Worker.Desktop;

internal static class DesktopStdioSession
{
    internal static async Task RunAsync(SqliteCatalog catalog, LocalWatchedIntakeGroup monitors, ProcessingPipeline processing, VerificationRunner verification, string inbox, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: false);
        using StreamWriter writer = new(Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        DesktopRequestReader frames = new(reader);
        LocalDesktopProtocol protocol = new(catalog, new LocalQuickCapture(catalog, inbox), processing: processing, verification: verification, group: monitors);

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

        await WorkerServices.RunAsync(cancellationToken, allowDesktopExit: true, ServeAsync, monitors.RunAsync, processing.RunAsync, verification.RunAsync);
    }
}
