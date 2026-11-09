using System.Globalization;
using System.Text.Json;

namespace Praxis.Storage;

internal readonly struct DesktopRequestFields(JsonElement root)
{
    public IReadOnlyList<MonthlyBudgetTarget> BudgetTargets()
    {
        if (!root.TryGetProperty("targets", out var value) ||
            value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 50)
        {
            throw new ArgumentException("Budget targets must be a bounded array.");
        }

        var targets = new List<MonthlyBudgetTarget>(value.GetArrayLength());
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("category", out var category) ||
                category.ValueKind != JsonValueKind.String ||
                category.GetString() is not { Length: > 0 and <= 80 } ||
                !entry.TryGetProperty("target", out var target) ||
                target.ValueKind != JsonValueKind.String ||
                target.GetString() is not { } amount ||
                amount.Length is < 1 or > 40 ||
                !decimal.TryParse(amount, NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var parsed) ||
                parsed < 0m || parsed > 1_000_000_000m)
            {
                throw new ArgumentException("Budget targets contain invalid typed values.");
            }

            targets.Add(new MonthlyBudgetTarget(category.GetString()!, parsed));
        }

        return targets;
    }

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
