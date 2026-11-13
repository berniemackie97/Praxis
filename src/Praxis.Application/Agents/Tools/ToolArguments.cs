using System.Globalization;
using System.Text.Json;

namespace Praxis.Application.Agents.Tools;

/// <summary>Bounded, typed reading of untrusted tool arguments. Invalid values raise <see cref="ArgumentException"/> with a fix-it message.</summary>
public readonly struct ToolArguments(JsonElement root)
{
    public JsonElement Root => root;

    public bool Has(string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null;

    public string? Text(string name, int maximumLength)
    {
        if (!Has(name))
        {
            return null;
        }

        JsonElement value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= maximumLength
            ? (text.Trim().Length == 0 ? null : text)
            : throw new ArgumentException($"'{name}' must be a string of at most {maximumLength} characters.");
    }

    public string RequireText(string name, int maximumLength) => Text(name, maximumLength) ?? throw new ArgumentException($"'{name}' is required.");

    public string Choice(string name, IReadOnlyList<string> choices, string fallback)
    {
        string? value = Text(name, 64);
        return value is null ? fallback
            : choices.Contains(value, StringComparer.Ordinal) ? value
            : throw new ArgumentException($"'{name}' must be one of: {string.Join(", ", choices)}.");
    }

    public int Integer(string name, int fallback, int minimum, int maximum)
    {
        if (!Has(name))
        {
            return fallback;
        }

        JsonElement value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && number >= minimum && number <= maximum
            ? number
            : throw new ArgumentException($"'{name}' must be an integer from {minimum} to {maximum}.");
    }

    public bool Boolean(string name, bool fallback) =>
        !Has(name) ? fallback
        : root.GetProperty(name).ValueKind is JsonValueKind.True or JsonValueKind.False ? root.GetProperty(name).GetBoolean()
        : throw new ArgumentException($"'{name}' must be true or false.");

    public DateOnly? Date(string name) =>
        Text(name, 10) is not { } text ? null
        : DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) ? date
        : throw new ArgumentException($"'{name}' must be a yyyy-MM-dd date.");

    public Guid? Guid(string name) =>
        Text(name, 36) is not { } text ? null
        : System.Guid.TryParse(text, out Guid id) && id != System.Guid.Empty ? id
        : throw new ArgumentException($"'{name}' must be an identifier returned by another tool.");

    public Guid RequireGuid(string name) => Guid(name) ?? throw new ArgumentException($"'{name}' is required.");

    public IReadOnlyList<string> Strings(string name, int maximumCount, int maximumLength)
    {
        if (!Has(name))
        {
            return [];
        }

        JsonElement value = root.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximumCount)
        {
            throw new ArgumentException($"'{name}' must be an array of at most {maximumCount} strings.");
        }

        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text && text.Length <= maximumLength
            ? text
            : throw new ArgumentException($"'{name}' items must be non-empty strings of at most {maximumLength} characters.")).ToArray();
    }

    public static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
