using System.Text.Json;
using Praxis.Application.Finance;
using Praxis.Application.Library;
using Praxis.Application.Sources;
using Praxis.Domain.Agents;
using Praxis.Domain.Finance;
using Praxis.Domain.Library;
using Praxis.Domain.Sources;

namespace Praxis.Application.Agents.Tools;

/// <summary>What happened in a period across sources: documents captured or revised, and money in and out by day.</summary>
public sealed class KnowledgeTimelineTool(ILibraryReader library, ISourceRepository sources, FinancialLedger ledger) : IAgentTool
{
    private const int MaximumSources = 2_000;

    public AgentToolDefinition Definition { get; } = new("knowledge_timeline",
        "Chronological view of a date range (max 366 days): sources captured or revised, and daily money in/out with notable transactions. Use it for 'what happened in March' or to orient before searching.",
        ToolArguments.Schema("""{"type":"object","properties":{"from":{"type":"string","description":"yyyy-MM-dd"},"to":{"type":"string","description":"yyyy-MM-dd"}},"required":["from","to"]}"""),
        ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        DateOnly from = args.Date("from") ?? throw new ArgumentException("'from' is required.");
        DateOnly to = args.Date("to") ?? throw new ArgumentException("'to' is required.");
        if (to < from || to.DayNumber - from.DayNumber > 366)
        {
            throw new ArgumentException("Use a range of at most 366 days with from <= to.");
        }

        List<object> captured = [];
        List<AgentCitation> citations = [];
        int withheld = 0;
        foreach (LibraryItem item in library.List(MaximumSources, 0, null).Where(item => item.SourceStatus == "active"))
        {
            if (sources.ReadProvenance(item.SourceId, item.Revision) is not SourceProvenance provenance)
            {
                continue;
            }

            DateOnly day = DateOnly.FromDateTime(provenance.ObservedUtc.UtcDateTime);
            if (day < from || day > to)
            {
                continue;
            }

            if (!context.MayDisclose(item.SourceId))
            {
                withheld++;
                continue;
            }

            captured.Add(new { date = day, source = Path.GetFileName(item.SourceLocator), item.SourceId, item.Revision, revised = item.Revision > 1 });
            citations.Add(new AgentCitation(item.SourceId, item.ItemId, item.Revision, "captured", Path.GetFileName(item.SourceLocator)));
        }

        IReadOnlyList<LedgerTransaction> money = ledger.Transactions(new LedgerQuery(from, to), FinanceLimits.MaximumQueryResults, context.MayDisclose);
        var days = money.GroupBy(transaction => (transaction.PostedOn, transaction.Amount.Currency)).OrderBy(group => group.Key.PostedOn).Select(group => new
        {
            date = group.Key.PostedOn,
            currency = group.Key.Currency,
            inflowMinor = group.Where(item => item.Amount.Minor > 0).Sum(item => item.Amount.Minor),
            outflowMinor = -group.Where(item => item.Amount.Minor < 0).Sum(item => item.Amount.Minor),
            largest = group.OrderByDescending(item => Math.Abs(item.Amount.Minor)).Take(3).Select(item => new { item.Description, item.Merchant, amountMinor = item.Amount.Minor })
        });
        return Task.FromResult(AgentToolOutput.Of(new { from, to, captured = captured.Take(200), money = days.Take(400) }, citations.Take(60).ToArray(),
            withheld > 0 ? [new WithheldNotice(withheld, WithheldNotice.DisclosurePolicy)] : null));
    }
}
