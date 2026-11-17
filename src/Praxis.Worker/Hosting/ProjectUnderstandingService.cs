using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Praxis.Application.Projects;
using Praxis.Application.Agents.Runtime;

namespace Praxis.Worker.Hosting;

/// <summary>
/// Re-derives code project understandings in the background: a pass every few minutes understands
/// projects whose files changed (each at most once per interval). Failures are logged and retried
/// on the next pass; they never stop the worker.
/// </summary>
public sealed partial class ProjectUnderstandingService(ProjectUnderstander understander, ILogger<ProjectUnderstandingService> logger, TimeProvider clock, AgentRunCoordinator runs)
    : BackgroundService
{
    private static readonly TimeSpan FirstPass = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstPass, clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    int understood = runs.HasActiveRuns ? 0 : await understander.RunAsync(stoppingToken);
                    if (understood > 0)
                    {
                        Understood(logger, understood);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    PassFailed(logger, exception.GetType().Name);
                }

                await Task.Delay(Interval, clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The worker is stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Understood {Count} code project(s).")]
    private static partial void Understood(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Code project understanding pass failed ({Error}); it is retried on the next pass.")]
    private static partial void PassFailed(ILogger logger, string error);
}
