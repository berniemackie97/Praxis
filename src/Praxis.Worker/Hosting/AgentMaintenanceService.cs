using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Memory;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Knowledge;
using Praxis.Application.Policy;
using Praxis.Domain.Agents;
using Praxis.Domain.Knowledge;

namespace Praxis.Worker.Hosting;

/// <summary>Resumes durable Dream work and reviews eligible receipts only during available periods.</summary>
internal sealed class AgentMaintenanceService(AgentRunCoordinator runs, AgentLearningReviewer learning, ISettingsStore settings,
    IAgentConversationRepository conversations, IKnowledgeReconsolidation revisions, ModelActivity activity, TimeProvider clock,
    ILogger<AgentMaintenanceService> logger) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        runs.RestorePending(recoverInterrupted: true);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (runs.HasActiveRuns || activity.Snapshot().Count != 0)
            {
                continue;
            }
            try
            {
                runs.RestorePending();
                if (runs.HasActiveRuns)
                {
                    continue;
                }
                await learning.RunNextAsync(stoppingToken);
                if (!runs.HasActiveRuns && activity.Snapshot().Count == 0)
                {
                    ScheduleDream();
                }
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Brain maintenance paused for this pass ({ErrorType}).", exception.GetType().Name);
            }
        }
    }

    private void ScheduleDream()
    {
        LearningSettings policy = learning.Settings;
        if (!policy.IdleDream || policy.Mode is LearningSettings.Off or LearningSettings.Manual)
        {
            return;
        }
        long watermark = revisions.Watermark().LatestRevisionId;
        if (settings.Read("idle_dream_watermark") == watermark.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            revisions.Revisions(RevisionVocabulary.Suggested, 1).Count == 0)
        {
            return;
        }
        if (DateTimeOffset.TryParse(settings.Read("idle_dream_last"), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out DateTimeOffset last) && clock.GetUtcNow() - last < TimeSpan.FromMinutes(policy.DreamIntervalMinutes))
        {
            return;
        }
        AgentBackendProfile? local = conversations.ReadBackends().FirstOrDefault(backend => backend.Enabled && backend.BackendId == "ollama" && backend.IsLocal);
        if (local is null)
        {
            return;
        }
        AgentConversation conversation = conversations.Create(local.BackendId, "Idle Brain review", policy.Model ?? local.Model, contextPreset: AgentContextPreset.Compact);
        runs.Start(conversation.ConversationId, "/dream");
        settings.Write("idle_dream_watermark", watermark.ToString(System.Globalization.CultureInfo.InvariantCulture));
        settings.Write("idle_dream_last", clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }
}
