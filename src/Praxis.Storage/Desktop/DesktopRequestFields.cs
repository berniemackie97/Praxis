using System.Text.Json;

namespace Praxis.Storage;

internal readonly struct DesktopRequestFields(JsonElement root)
{
    public bool RequireBoolean(string property)
    {
        if (!root.TryGetProperty(property, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"The {property} field must be true or false.");
        }

        return value.GetBoolean();
    }

    public string? OptionalText(string property, int limit, string invalidMessage)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text || text.Length > limit)
        {
            throw new ArgumentException(invalidMessage);
        }

        return text;
    }

    public Guid? OptionalGuid(string property) =>
        root.TryGetProperty(property, out _) ? RequireGuid(property) : null;

    public Guid RequireGuid(string property)
    {
        var text = RequireString(property, 36);
        return Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty
            ? id : throw new ArgumentException("A valid item or source identifier is required.");
    }

    public string RequireString(string property, int limit)
    {
        if (!root.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text ||
            text.Length is < 1 || text.Length > limit)
        {
            throw new ArgumentException($"The {property} field is invalid.");
        }

        return text;
    }

    public int IntegerOrDefault(string property, int fallback, int min, int max) =>
        OptionalInteger(property, min, max) ?? fallback;

    public int? OptionalInteger(string property, int min, int max)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number) || number < min || number > max)
        {
            throw new ArgumentException($"The {property} field is outside its limit.");
        }

        return number;
    }
}
