using System.Globalization;
using System.Text.Json;
using Praxis.Application.Agents.Compute;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Tools;

/// <summary>Exact decimal arithmetic for scenarios and derived figures, so the model never computes numbers itself.</summary>
public sealed class ComputeEvaluateTool : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("compute_evaluate",
        "Evaluate exact decimal arithmetic: + - * / ( ) , percentages like 30%, named variables, and round(x, places), floor, ceil, abs, min, max, sum. Use for every derived number (savings, ratios, projections). Pass several labelled expressions at once.",
        ToolArguments.Schema("""
            {"type":"object","properties":{
              "expressions":{"type":"object","additionalProperties":{"type":"string"},"description":"label -> expression; later expressions may use earlier labels as variables"},
              "variables":{"type":"object","additionalProperties":{"type":"string"}}},"required":["expressions"]}
            """), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("expressions", out JsonElement expressions) || expressions.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("'expressions' must be an object of label -> expression.");
        }

        Dictionary<string, decimal> scope = new(StringComparer.Ordinal);
        if (arguments.TryGetProperty("variables", out JsonElement variables) && variables.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty variable in variables.EnumerateObject().Take(100))
            {
                scope[Label(variable.Name)] = DecimalExpression.Evaluate(variable.Value.ValueKind == JsonValueKind.Number ? variable.Value.GetRawText() : variable.Value.GetString() ?? "");
            }
        }

        Dictionary<string, string> results = new(StringComparer.Ordinal);
        foreach (JsonProperty expression in expressions.EnumerateObject().Take(50))
        {
            decimal value = DecimalExpression.Evaluate(expression.Value.GetString() ?? throw new ArgumentException($"'{expression.Name}' must be a string."), scope);
            scope[Label(expression.Name)] = value;
            results[expression.Name] = value.ToString(CultureInfo.InvariantCulture);
        }

        return Task.FromResult(AgentToolOutput.Of(new { results }));
    }

    private static string Label(string name) =>
        name.Length is > 0 and <= 64 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? name
            : throw new ArgumentException($"Label '{name}' must be letters, digits or underscores and start with a letter.");
}
