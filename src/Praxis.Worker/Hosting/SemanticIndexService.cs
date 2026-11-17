using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Policy;
using Praxis.Application.Search;

namespace Praxis.Worker.Hosting;

/// <summary>Low-priority local vector upkeep. An active bounded batch drains when interactive work starts.</summary>
internal sealed class SemanticIndexService(
    SemanticIndexMaintenance index, AgentRunCoordinator chats, ModelActivity activity, TimeProvider clock,
    ILogger<SemanticIndexService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (chats.HasActiveRuns || activity.Snapshot().Count != 0)
            {
                continue;
            }

            try
            {
                int indexed = await index.RunNextAsync(stoppingToken);
                if (indexed > 0)
                {
                    logger.LogInformation("Updated {Segments} local semantic segments.", indexed);
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Semantic indexing paused for this pass ({ErrorType}).", exception.GetType().Name);
            }
        }
    }
}
