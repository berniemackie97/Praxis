using System.Globalization;
using System.Text.Json;
using Praxis.Application.Finance;
using Praxis.Application.Library;
using Praxis.Application.Sources;
using Praxis.Application.Understanding;
using Praxis.Domain.Agents;
using Praxis.Domain.Finance;
using Praxis.Domain.Library;
using Praxis.Domain.Sources;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Agents.Tools;

/// <summary>
/// What happened (or is scheduled) in a period across sources: documents captured or revised,
/// events and deadlines the documents state, and money in and out by day.
/// </summary>
public sealed class KnowledgeTimelineTool(ILibraryReader library, ISourceRepository sources, FinancialLedger ledger, IUnderstandingRepository? understanding = null)
    : IAgentTool
{
    private const int MaximumSources = 2_000;

    public AgentToolDefinition Definition { get; } = new("knowledge_timeline",
        "Chronological view of a date range (max 366 days, past or future): sources captured or revised, events and deadlines stated in documents, and daily money in/out with notable transactions. Use it for 'what happened in March', 'what is coming up next week' or to orient before searching.",
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
        foreach (LibraryItem item in Library().Where(item => item.SourceStatus == "active"))
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

        List<object> scheduled = [];
        foreach (StoredRecord record in understanding?.ReadRecords(null, 20_000) ?? [])
        {
            if (record.Kind is not ("event" or "deadline") || record.SortKey is not { Length: >= 10 } key ||
                !DateOnly.TryParseExact(key[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day) || day < from || day > to)
            {
                continue;
            }

            if (!context.MayDisclose(record.SourceId))
            {
                withheld++;
                continue;
            }

            scheduled.Add(new
            {
                date = day,
                at = key.Length >= 16 ? key[11..16] : null,
                record.Kind,
                title = record.Fields.GetValueOrDefault("title"),
                location = record.Fields.GetValueOrDefault("location"),
                amountMinor = record.Fields.GetValueOrDefault("amount_minor"),
                currency = record.Fields.GetValueOrDefault("currency"),
                source = record.SourceName
            });
            citations.Add(new AgentCitation(record.SourceId, record.ItemId, record.Revision,
                $"chars:{record.EvidenceOffset}-{record.EvidenceOffset + record.EvidenceLength}", record.SourceName));
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
        return Task.FromResult(AgentToolOutput.Of(new { from, to, scheduled = scheduled.Take(300), captured = captured.Take(200), money = days.Take(400) }, citations.Take(60).ToArray(),
            withheld > 0 ? [new WithheldNotice(withheld, WithheldNotice.DisclosurePolicy)] : null));
    }

    /// <summary>Library pages of at most 200 items, up to the source bound.</summary>
    private IEnumerable<LibraryItem> Library()
    {
        const int Page = 200;
        for (int offset = 0; offset < MaximumSources; offset += Page)
        {
            IReadOnlyList<LibraryItem> page = library.List(Page, offset, null);
            foreach (LibraryItem item in page)
            {
                yield return item;
            }

            if (page.Count < Page)
            {
                yield break;
            }
        }
    }
}
