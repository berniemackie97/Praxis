using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Praxis.Application.Agents.Memory;

namespace Praxis.Worker.Hosting;

internal sealed class ChatMemoryService(ChatMemoryMaintenance memory, ILogger<ChatMemoryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(4));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await memory.RunNextAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                // Never log the dialogue or model response.
                logger.LogWarning("Local chat memory paused for this pass ({ErrorType}).", exception.GetType().Name);
            }
        }
    }
}
