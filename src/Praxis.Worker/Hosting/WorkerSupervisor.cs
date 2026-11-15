using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Praxis.Application.Knowledge;
using Praxis.Application.Processing;
using Praxis.Application.Verification;
using Praxis.Infrastructure.Intake;
using Praxis.Worker.Desktop;

namespace Praxis.Worker.Hosting;

/// <summary>
/// Runs folder intake, processing, verification, reconsolidation and (in desktop mode) the stdio session as one
/// group: when any of them ends the others are canceled and awaited, then the host stops. The
/// outcome is recorded for the process exit code instead of being logged with its exception.
/// </summary>
internal sealed class WorkerSupervisor(
    WorkerOptions options, WatchedFolderIntakeGroup intake, ProcessingPipeline processing, VerificationRunner verification,
    ReconsolidationRunner reconsolidation,
    DesktopStdioSession session, WorkerOutcome outcome, IHostApplicationLifetime lifetime,
    ILogger<WorkerSupervisor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Worker supervisor started.");
            if (options.DesktopMode)
            {
                await WorkerServices.RunAsync(stoppingToken, allowDesktopExit: true,
                    session.RunAsync, intake.RunAsync, processing.RunAsync, verification.RunAsync, reconsolidation.RunAsync);
            }
            else
            {
                await WorkerServices.RunAsync(stoppingToken, allowDesktopExit: false,
                    intake.RunAsync, processing.RunAsync, verification.RunAsync, reconsolidation.RunAsync);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            outcome.Fail(exception);
        }
        finally
        {
            logger.LogInformation("Worker supervisor stopped.");
            lifetime.StopApplication();
        }
    }
}
