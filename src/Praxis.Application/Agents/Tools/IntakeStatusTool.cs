using System.Text.Json;
using Praxis.Application.Finance;
using Praxis.Application.Policy;
using Praxis.Application.Processing;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;
using Praxis.Domain.Processing;

namespace Praxis.Application.Agents.Tools;

/// <summary>What intake is doing: backlog, items held for disclosure decisions, failures and unusable financial files.</summary>
public sealed class IntakeStatusTool(IProcessingQueue processing, IPolicyRepository policy, FinancialLedger ledger) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("intake_status",
        "Show intake progress: queued/running/failed processing counts, items waiting for the owner's disclosure approval (with deadlines), recent failures and financial files that could not be used. Use it to explain why something is missing.",
        ToolArguments.Schema("""{"type":"object","properties":{}}"""), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ProcessingBacklog backlog = processing.Backlog();
        IReadOnlyList<DisclosureHold> holds = policy.ReadHolds(openOnly: true, 50);
        return Task.FromResult(AgentToolOutput.Of(new
        {
            backlog = new { backlog.Queued, backlog.Running, backlog.Failed },
            awaitingApproval = holds.Where(hold => context.MayDisclose(hold.SourceId) || context.BackendIsLocal)
                .Select(hold => new { hold.SourceName, hold.Category, provider = hold.ProviderId, hold.DeadlineUtc, step = hold.ProcessorKey }),
            failures = processing.List(500).Where(job => job.State == "failed").TakeLast(20)
                .Select(job => new { job.ItemId, job.Revision, processor = job.ProcessorKey, job.FailureCode }),
            unusableFinancialFiles = ledger.Documents(100, context.MayDisclose).Where(document => document.Outcome != "completed")
                .Select(document => new { document.SourceName, document.ReasonCode })
        }));
    }
}
