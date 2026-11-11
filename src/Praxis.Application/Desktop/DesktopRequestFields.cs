using System.Text.Json;

namespace Praxis.Application.Desktop;

/// <summary>Validated access to the fields of one desktop request object.</summary>
public readonly struct DesktopRequestFields(JsonElement root)
{
    public bool RequireBoolean(string property)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"The {property} field must be true or false.");
        }

        return value.GetBoolean();
    }

    public string? OptionalText(string property, int limit, string invalidMessage)
    {
        if (!root.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > limit)
        {
            throw new ArgumentException(invalidMessage);
        }

        return text;
    }

    public Guid? OptionalGuid(string property) => root.TryGetProperty(property, out _) ? RequireGuid(property) : null;

    public Guid RequireGuid(string property)
    {
        string text = RequireString(property, 36);
        return Guid.TryParseExact(text, "D", out Guid id) && id != Guid.Empty ? id : throw new ArgumentException("A valid item or source identifier is required.");
    }

    public string RequireString(string property, int limit)
    {
        if (!root.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length is < 1 || text.Length > limit)
        {
            throw new ArgumentException($"The {property} field is invalid.");
        }

        return text;
    }

    public int IntegerOrDefault(string property, int fallback, int min, int max) => OptionalInteger(property, min, max) ?? fallback;

    public int? OptionalInteger(string property, int min, int max)
    {
        if (!root.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number) || number < min || number > max)
        {
            throw new ArgumentException($"The {property} field is outside its limit.");
        }

        return number;
    }

    /// <summary>An optional array of integers within bounds, at most <paramref name="maximumCount"/> long.</summary>
    public IReadOnlyList<int> OptionalIntegers(string property, int min, int max, int maximumCount)
    {
        if (!root.TryGetProperty(property, out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximumCount)
        {
            throw new ArgumentException($"The {property} field must be a bounded array.");
        }

        List<int> numbers = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (!item.TryGetInt32(out int number) || number < min || number > max)
            {
                throw new ArgumentException($"The {property} field contains an invalid value.");
            }

            numbers.Add(number);
        }

        return numbers;
    }

    /// <summary>An optional array of nonempty strings, each at most <paramref name="limit"/> characters.</summary>
    public IReadOnlyList<string> OptionalStrings(string property, int limit, int maximumCount)
    {
        if (!root.TryGetProperty(property, out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximumCount)
        {
            throw new ArgumentException($"The {property} field must be a bounded array.");
        }

        List<string> texts = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: >= 1 } text || text.Length > limit)
            {
                throw new ArgumentException($"The {property} field contains an invalid value.");
            }

            texts.Add(text);
        }

        return texts;
    }
}
