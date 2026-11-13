using System.Text.Json;
using Praxis.Domain.Finance;

namespace Praxis.Application.Finance.Categorization;

/// <summary>
/// Accepts annotations only for requested rows, from the closed vocabulary, consistent with
/// direction, with a bounded merchant name. Returns null when the answer is unusable.
/// </summary>
public static class AnnotationReader
{
    private static readonly HashSet<string> InflowOnly = [SpendingCategories.Income, SpendingCategories.Interest, SpendingCategories.Refund];

    public static IReadOnlyList<(int Ordinal, string Merchant, string Category)>? Read(string content, IReadOnlyList<ParsedTransaction> rows)
    {
        Dictionary<int, ParsedTransaction> requested = rows.ToDictionary(row => row.Ordinal);
        try
        {
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 6 });
            if (!document.RootElement.TryGetProperty("annotations", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            Dictionary<int, (int, string, string)> accepted = [];
            foreach (JsonElement item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out JsonElement id) || !id.TryGetInt32(out int ordinal) ||
                    !requested.TryGetValue(ordinal, out ParsedTransaction? row) ||
                    !item.TryGetProperty("category", out JsonElement categoryValue) || categoryValue.GetString() is not { } category ||
                    !SpendingCategories.All.Contains(category, StringComparer.Ordinal) ||
                    !item.TryGetProperty("merchant", out JsonElement merchantValue) || merchantValue.GetString()?.Trim() is not { Length: > 0 and <= 128 } merchant)
                {
                    continue;
                }

                bool inflow = row.Amount.Minor > 0;
                if (!inflow && InflowOnly.Contains(category))
                {
                    continue;
                }

                accepted.TryAdd(ordinal, (ordinal, merchant, category));
            }

            return accepted.Values.ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
