using System.Text.Json;
using System.Text.Json.Nodes;

namespace Praxis.Infrastructure.Inference.Subscription;

/// <summary>
/// Converts a response schema to the strict form OpenAI structured outputs require: every object
/// forbids additional properties and lists all of its properties as required, and properties that
/// were optional become nullable. A strict answer therefore carries nulls where a field is absent,
/// which Praxis's readers already ignore.
/// </summary>
internal static class StrictSchema
{
    public static string Convert(JsonElement schema)
    {
        JsonNode node = JsonNode.Parse(schema.GetRawText()) ?? throw new ArgumentException("The schema is empty.", nameof(schema));
        Tighten(node);
        return node.ToJsonString();
    }

    private static void Tighten(JsonNode? node)
    {
        if (node is not JsonObject schema)
        {
            return;
        }

        if (schema["properties"] is JsonObject properties)
        {
            HashSet<string> required = schema["required"] is JsonArray listed
                ? listed.Select(item => item?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];
            foreach ((string name, JsonNode? property) in properties.ToArray())
            {
                Tighten(property);
                if (!required.Contains(name) && property is JsonObject optional)
                {
                    MakeNullable(optional);
                }
            }

            schema["required"] = new JsonArray(properties.Select(property => (JsonNode?)JsonValue.Create(property.Key)).ToArray());
            schema["additionalProperties"] = false;
        }

        if (schema["items"] is { } items)
        {
            Tighten(items);
        }
    }

    private static void MakeNullable(JsonObject property)
    {
        switch (property["type"])
        {
            case JsonValue single when single.GetValue<string>() is { } type && type != "null":
                property["type"] = new JsonArray(JsonValue.Create(type), JsonValue.Create("null"));
                if (property["enum"] is JsonArray values && !values.Any(value => value is null))
                {
                    values.Add(null);
                }

                break;
            case JsonArray types when !types.Any(type => type?.GetValue<string>() == "null"):
                types.Add(JsonValue.Create("null"));
                break;
        }
    }
}
