using System.Text.Json;
using Praxis.Application.Agents.Data;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Tools;

/// <summary>
/// One exact query language over every structured dataset: filters, grouping, metrics, sorting
/// and paging. All arithmetic happens here, in integer minor units for money.
/// </summary>
public sealed class DataQueryTool(IEnumerable<IDataset> datasets) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("data_query",
        """
        Query a structured dataset exactly (see data_describe). filters: [{field, op: eq|ne|in|gt|gte|lt|lte|contains|between, values:[...]}];
        groupBy: up to 3 fields; metrics: [{function: count|sum|min|max|avg, field}]; sort: field or metric name (e.g. sum_amount);
        descending; limit (<=200); offset. Money is integer minor units (cents), negative = out; totals are always per currency.
        Without groupBy/metrics returns matching rows with evidence citations. Never add or multiply numbers yourself: query or use compute_evaluate.
        """,
        ToolArguments.Schema("""
            {"type":"object","properties":{
              "dataset":{"type":"string"},
              "filters":{"type":"array","maxItems":12,"items":{"type":"object","properties":{"field":{"type":"string"},"op":{"type":"string"},"values":{"type":"array","items":{"type":"string"}}},"required":["field","op","values"]}},
              "groupBy":{"type":"array","maxItems":3,"items":{"type":"string"}},
              "metrics":{"type":"array","maxItems":6,"items":{"type":"object","properties":{"function":{"type":"string"},"field":{"type":"string"}},"required":["function"]}},
              "sort":{"type":"string"},"descending":{"type":"boolean"},
              "limit":{"type":"integer","minimum":1,"maximum":200},"offset":{"type":"integer","minimum":0}},"required":["dataset"]}
            """), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        string name = args.RequireText("dataset", 64);
        IDataset dataset = datasets.FirstOrDefault(item => item.Name == name)
            ?? throw new ArgumentException($"Unknown dataset '{name}'. Datasets: {string.Join(", ", datasets.Select(item => item.Name))}.");
        List<DataFilter> filters = [];
        if (args.Has("filters"))
        {
            foreach (JsonElement filter in arguments.GetProperty("filters").EnumerateArray().Take(12))
            {
                ToolArguments clause = new(filter);
                filters.Add(new DataFilter(clause.RequireText("field", 64), clause.RequireText("op", 16), clause.Strings("values", 50, 200)));
            }
        }

        List<DataMetric> metrics = [];
        if (args.Has("metrics"))
        {
            foreach (JsonElement metric in arguments.GetProperty("metrics").EnumerateArray().Take(6))
            {
                ToolArguments clause = new(metric);
                metrics.Add(new DataMetric(clause.RequireText("function", 8), clause.Text("field", 64)));
            }
        }

        DatasetSnapshot snapshot = dataset.Snapshot(context);
        object result = DataQueryEngine.Run(dataset, snapshot, filters, args.Strings("groupBy", 3, 64), metrics, args.Text("sort", 64),
            args.Boolean("descending", false), args.Integer("limit", 50, 1, 200), args.Integer("offset", 0, 0, 1_000_000),
            out IReadOnlyList<AgentCitation> citations);
        return Task.FromResult(AgentToolOutput.Of(result, citations,
            snapshot.Withheld > 0 ? [new WithheldNotice(snapshot.Withheld, WithheldNotice.DisclosurePolicy)] : null));
    }
}
