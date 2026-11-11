using System.Text.Json;
using System.Text.Json.Nodes;

namespace Praxis.Application.Verification;

/// <summary>
/// The exact JSON a verifier receives. Built deterministically from catalog evidence, then
/// narrowed by the owner before approval: excluded items are removed and every owner-supplied
/// phrase is replaced in all text values. What is stored is what is sent.
/// </summary>
public static class VerificationPayload
{
    public const string Redacted = "[REDACTED]";
    public const int ContextCharacters = 220;
    public const int MaximumBytes = 60_000;

    public static string Serialize(IReadOnlyList<JsonObject> items)
    {
        var payload = new JsonObject { ["items"] = new JsonArray(items.Select(item => (JsonNode)item).ToArray()) };
        var text = payload.ToJsonString();
        return text.Length <= MaximumBytes
            ? text
            : throw new InvalidOperationException("The verification request is too large; verify fewer items.");
    }

    /// <summary>Text surrounding an evidence span, on one line, for the verifier's judgement.</summary>
    public static string Context(string content, int offset, int length)
    {
        var start = Math.Max(0, offset - ContextCharacters);
        var end = Math.Min(content.Length, offset + length + ContextCharacters);
        if (start > 0 && char.IsLowSurrogate(content[start]))
        {
            start--;
        }

        if (end < content.Length && char.IsLowSurrogate(content[end]))
        {
            end++;
        }

        return string.Join(' ', content[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Removes excluded ordinals and redacts phrases; returns the payload and kept ordinals.</summary>
    public static (string Payload, IReadOnlySet<int> Kept) Narrow(
        string payload, IReadOnlyCollection<int> excluded, IReadOnlyCollection<string> redactions)
    {
        if (redactions.Any(phrase => string.IsNullOrWhiteSpace(phrase) || phrase.Length > 200))
        {
            throw new ArgumentException("Redactions must be one to two hundred characters.", nameof(redactions));
        }

        var root = JsonNode.Parse(payload)!.AsObject();
        var items = root["items"]!.AsArray();
        foreach (var item in items.Where(item => excluded.Contains(Ordinal(item!))).ToArray())
        {
            items.Remove(item);
        }

        if (items.Count == 0)
        {
            throw new InvalidOperationException("Every item was excluded; decline the request instead.");
        }

        Redact(root, redactions);
        return (root.ToJsonString(), items.Select(item => Ordinal(item!)).ToHashSet());
    }

    public static int Ordinal(JsonNode item) => item["ordinal"]!.GetValue<int>();

    private static void Redact(JsonNode node, IReadOnlyCollection<string> redactions)
    {
        switch (node)
        {
            case JsonObject value:
                foreach (var property in value.ToArray())
                {
                    if (property.Value is JsonValue text && text.GetValueKind() == JsonValueKind.String)
                    {
                        value[property.Key] = Apply(text.GetValue<string>(), redactions);
                    }
                    else if (property.Value is not null)
                    {
                        Redact(property.Value, redactions);
                    }
                }

                break;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is JsonValue text && text.GetValueKind() == JsonValueKind.String)
                    {
                        array[index] = Apply(text.GetValue<string>(), redactions);
                    }
                    else if (array[index] is { } child)
                    {
                        Redact(child, redactions);
                    }
                }

                break;
        }
    }

    private static string Apply(string text, IReadOnlyCollection<string> redactions) =>
        redactions.Aggregate(text, (current, phrase) =>
            current.Replace(phrase.Trim(), Redacted, StringComparison.OrdinalIgnoreCase));
}
