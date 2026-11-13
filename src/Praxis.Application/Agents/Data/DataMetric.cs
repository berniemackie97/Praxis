namespace Praxis.Application.Agents.Data;

/// <summary>An aggregate over a field: count, sum, min, max or avg.</summary>
public sealed record DataMetric(string Function, string? Field)
{
    public static readonly IReadOnlyList<string> Functions = ["count", "sum", "min", "max", "avg"];

    public string Name => Field is null ? Function : $"{Function}_{Field}";
}
