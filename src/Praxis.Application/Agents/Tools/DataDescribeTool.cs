using System.Text.Json;
using Praxis.Application.Agents.Data;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Tools;

/// <summary>What structured data exists: datasets, fields, coverage and known gaps.</summary>
public sealed class DataDescribeTool(IEnumerable<IDataset> datasets) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("data_describe",
        "List the structured datasets you can query exactly with data_query (e.g. transactions, accounts, financial_documents): fields, row counts, date coverage and known gaps such as missing months or files awaiting approval. Call this before answering questions about money or other structured records.",
        ToolArguments.Schema("""{"type":"object","properties":{"dataset":{"type":"string"}}}"""), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        string? only = new ToolArguments(arguments).Text("dataset", 64);
        IDataset[] selected = datasets.Where(dataset => only is null || dataset.Name == only).ToArray();
        if (selected.Length == 0)
        {
            throw new ArgumentException($"Unknown dataset. Datasets: {string.Join(", ", datasets.Select(dataset => dataset.Name))}.");
        }

        List<WithheldNotice> withheld = [];
        var described = selected.Select(dataset =>
        {
            DatasetSnapshot snapshot = dataset.Snapshot(context);
            if (snapshot.Withheld > 0)
            {
                withheld.Add(new WithheldNotice(snapshot.Withheld, WithheldNotice.DisclosurePolicy));
            }

            DateOnly[] dates = snapshot.Rows.SelectMany(row => row.Values.Values.OfType<DateOnly>()).ToArray();
            return new
            {
                dataset.Name,
                dataset.Description,
                rows = snapshot.Rows.Count,
                withheldByPolicy = snapshot.Withheld,
                coverage = dates.Length == 0 ? null : new { from = dates.Min(), to = dates.Max() },
                fields = dataset.Fields.Select(field => new { field.Name, field.Type, field.Description, field.Groupable }),
                gaps = snapshot.Gaps
            };
        }).ToArray();
        return Task.FromResult(AgentToolOutput.Of(new { datasets = described }, withheld: withheld));
    }
}
