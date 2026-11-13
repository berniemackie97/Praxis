using System.Text.Json;
using System.Text.Json.Serialization;

namespace Praxis.Application.Agents.Tools;

/// <summary>Compact camelCase JSON without nulls: the token-efficient shape models read best.</summary>
public static class ToolJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
