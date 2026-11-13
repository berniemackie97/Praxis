namespace Praxis.Application.Agents.Data;

/// <summary>A filter clause: field, operator and value(s).</summary>
public sealed record DataFilter(string Field, string Op, IReadOnlyList<string> Values)
{
    public static readonly IReadOnlyList<string> Operators = ["eq", "ne", "in", "gt", "gte", "lt", "lte", "contains", "between"];
}
