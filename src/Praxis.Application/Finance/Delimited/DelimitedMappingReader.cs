using System.Text.Json;
using Praxis.Domain.Finance;

namespace Praxis.Application.Finance.Delimited;

/// <summary>Reads a mapping answer, rejecting out-of-range columns, unknown enums and inconsistent money columns.</summary>
public static class DelimitedMappingReader
{
    public static DelimitedMapping? Read(string content, int columnCount, int rowCount)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 6 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("isFinancial", out JsonElement financial) ||
                financial.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return null;
            }

            if (!financial.GetBoolean())
            {
                return new DelimitedMapping(false, -1, 0, DateOrder.MonthFirst, [], null, null, null, false, null, null, null, "USD", null, AccountKinds.Unknown);
            }

            int? Column(string name) =>
                root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int index) &&
                index >= 0 && index < columnCount ? index : null;

            int header = root.TryGetProperty("headerRow", out JsonElement headerValue) && headerValue.TryGetInt32(out int row) ? row : -2;
            DateOrder? order = Text(root, "dateOrder") switch
            {
                "month_first" => DateOrder.MonthFirst,
                "day_first" => DateOrder.DayFirst,
                "year_first" => DateOrder.YearFirst,
                _ => null
            };
            int? date = Column("dateColumn");
            int? amount = Column("amountColumn");
            int? debit = Column("debitColumn");
            int? credit = Column("creditColumn");
            string currency = (Text(root, "currency") ?? "USD").ToUpperInvariant();
            string kind = Text(root, "accountKind") ?? AccountKinds.Unknown;
            int[] descriptions = root.TryGetProperty("descriptionColumns", out JsonElement list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(item => item.TryGetInt32(out int index) && index >= 0 && index < columnCount)
                    .Select(item => item.GetInt32()).Distinct().Take(4).ToArray()
                : [];
            string? lastFour = Text(root, "accountLastFour");
            if (header < -1 || header >= rowCount || order is null || date is null || !CurrencyExponents.IsValidCode(currency) ||
                !AccountKinds.All.Contains(kind, StringComparer.Ordinal) || (amount is null) == (debit is null && credit is null) ||
                (lastFour is not null && (lastFour.Length != 4 || !lastFour.All(char.IsAsciiDigit))))
            {
                return null;
            }

            return new DelimitedMapping(true, header, date.Value, order.Value, descriptions, amount, debit, credit,
                Text(root, "amountSign") == "inverted", Column("balanceColumn"), Column("statusColumn"), Column("accountColumn"),
                currency, lastFour, kind);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
