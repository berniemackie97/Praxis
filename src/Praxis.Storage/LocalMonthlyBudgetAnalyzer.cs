using System.Globalization;
using System.Text;

namespace Praxis.Storage;

public sealed record MonthlyBudgetCategory(
    string Category, decimal PostedSpending, decimal PlannedBills, int RowCount);

public sealed record MonthlyBudgetEvidence(
    int SourceRow, string RecordId, DateOnly Date, string Kind, string Status,
    decimal Amount, string Category, string Description);

public sealed record MonthlyBudgetReport(
    Guid SourceId, Guid ItemId, int Revision, string SourceSha256,
    int Year, int Month, string Currency, decimal PostedIncome,
    decimal PostedSpending, decimal PlannedBills, decimal PendingNetSpending,
    decimal ActualBalance, decimal BalanceAfterPlannedBills,
    int IncludedRows, int IgnoredDuplicateRows, int PendingRows,
    int TransfersExcluded, bool EvidenceTruncated,
    IReadOnlyList<MonthlyBudgetCategory> Categories,
    IReadOnlyList<MonthlyBudgetEvidence> Evidence);

public sealed class LocalMonthlyBudgetAnalyzer(LocalCatalog catalog)
{
    public const int MaximumBytes = 1024 * 1024;
    private const int MaximumRows = 5_000;
    private const int MaximumEvidenceRows = 100;

    public async Task<MonthlyBudgetReport> AnalyzeAsync(
        Guid sourceId, int year, int month, CancellationToken cancellationToken = default)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month), "Choose a month from 2000 through 2100.");
        }

        var source = await catalog.ReadBudgetSourceAsync(sourceId, cancellationToken);
        var rows = BudgetCsvParser.Parse(source.Content, MaximumRows);
        if (rows.Count == 0 || !rows[0].Cells.SequenceEqual(
            ["date", "record_id", "kind", "amount", "currency", "category", "description", "status"]))
        {
            throw new InvalidDataException("Budget CSV headers must match the documented eight-column format.");
        }

        var categories = new Dictionary<string, (decimal Spending, decimal Bills, int Count)>(
            StringComparer.Ordinal);
        var deduplicated = new Dictionary<string, MonthlyBudgetEvidence>(StringComparer.Ordinal);
        var evidence = new List<MonthlyBudgetEvidence>();
        var actualIncome = 0m;
        var postedSpending = 0m;
        var plannedBills = 0m;
        var pendingNetSpending = 0m;
        var included = 0;
        var duplicates = 0;
        var pending = 0;
        var transfers = 0;

        foreach (var row in rows.Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fields = row.Cells;
            if (fields.Length != 8)
            {
                throw new InvalidDataException($"Budget CSV row {row.Line} has an incorrect column count.");
            }

            if (!DateOnly.TryParseExact(fields[0], "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                !decimal.TryParse(fields[3], NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var amount) ||
                amount <= 0m || amount > 1_000_000_000m || decimal.Round(amount, 2) != amount)
            {
                throw new InvalidDataException($"Budget CSV row {row.Line} has invalid date or amount.");
            }

            var id = fields[1];
            var kind = fields[2];
            var currency = fields[4];
            var category = fields[5];
            var description = fields[6];
            var status = fields[7];
            if (id.Length is < 1 or > 100 || id.Any(c => !char.IsAsciiLetterOrDigit(c)
                    && c is not ('-' or '_' or '.' or ':')) ||
                kind is not ("income" or "expense" or "bill" or "refund" or "transfer") ||
                currency != "USD" ||
                category.Length is < 1 or > 80 || category.Any(char.IsControl) ||
                description.Length is < 1 or > 200 || description.Any(char.IsControl) ||
                status is not ("posted" or "pending" or "estimated") ||
                (kind == "bill") != (status == "estimated"))
            {
                throw new InvalidDataException($"Budget CSV row {row.Line} has invalid typed fields.");
            }

            var entry = new MonthlyBudgetEvidence(
                row.Line, id, date, kind, status, amount, category, description);
            if (deduplicated.TryGetValue(id, out var existing))
            {
                if (existing with { SourceRow = row.Line } != entry)
                {
                    throw new InvalidDataException(
                        $"Budget CSV row {row.Line} reuses a transaction identity with conflicting data.");
                }

                duplicates++;
                continue;
            }

            deduplicated.Add(id, entry);
            if (date.Year != year || date.Month != month)
            {
                continue;
            }

            if (kind == "transfer")
            {
                transfers++;
                continue;
            }

            included++;
            if (status == "pending")
            {
                pending++;
                pendingNetSpending += kind switch
                {
                    "income" => -amount,
                    "refund" => -amount,
                    _ => amount
                };
            }
            else if (kind == "income")
            {
                actualIncome += amount;
            }
            else if (kind == "bill")
            {
                plannedBills += amount;
            }
            else if (kind == "refund")
            {
                postedSpending -= amount;
            }
            else
            {
                postedSpending += amount;
            }

            if (kind is "expense" or "refund" or "bill" && status != "pending")
            {
                categories.TryGetValue(category, out var value);
                categories[category] = (
                    value.Spending + (kind == "expense" ? amount :
                        kind == "refund" ? -amount : 0m),
                    value.Bills + (kind == "bill" ? amount : 0m), value.Count + 1);
            }

            if (evidence.Count < MaximumEvidenceRows)
            {
                evidence.Add(entry);
            }
        }

        catalog.AssertBudgetSourceCurrent(source);
        var actualBalance = actualIncome - postedSpending;
        return new MonthlyBudgetReport(source.SourceId, source.ItemId, source.Revision,
            source.Sha256, year, month, "USD", actualIncome, postedSpending,
            plannedBills, pendingNetSpending, actualBalance, actualBalance - plannedBills,
            included, duplicates, pending, transfers, included > evidence.Count,
            categories.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new MonthlyBudgetCategory(
                    pair.Key, pair.Value.Spending, pair.Value.Bills, pair.Value.Count))
                .ToArray(), evidence.AsReadOnly());
    }
}

internal sealed record BudgetCsvRow(int Line, string[] Cells);

internal static class BudgetCsvParser
{
    internal static IReadOnlyList<BudgetCsvRow> Parse(string text, int maxRows)
    {
        var rows = new List<BudgetCsvRow>();
        var columns = new List<string>(8);
        var field = new StringBuilder();
        var line = 1;
        var rowStart = 1;
        var quoted = false;
        var closedQuote = false;
        var started = false;

        for (var i = 0; i <= text.Length; i++)
        {
            var end = i == text.Length;
            var c = end ? '\n' : text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                        closedQuote = true;
                    }
                }
                else
                {
                    field.Append(c);
                    if (c == '\n')
                    {
                        line++;
                    }
                }
            }
            else if (c == ',' || c == '\n' || c == '\r' || end)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                columns.Add(field.ToString());
                field.Clear();
                closedQuote = false;
                if (columns.Count > 8)
                {
                    throw new InvalidDataException($"Budget CSV row {rowStart} has too many columns.");
                }

                if (c != ',')
                {
                    if (started || columns.Any(value => value.Length > 0))
                    {
                        rows.Add(new BudgetCsvRow(rowStart, columns.ToArray()));
                        if (rows.Count > maxRows + 1)
                        {
                            throw new InvalidDataException("The financial CSV exceeds its row budget.");
                        }
                    }

                    columns.Clear();
                    started = false;
                    line++;
                    rowStart = line;
                }
                else
                {
                    started = true;
                }
            }
            else if (c == '"' && field.Length == 0 && !closedQuote)
            {
                quoted = true;
                started = true;
            }
            else if (closedQuote || c == '"')
            {
                throw new InvalidDataException($"Budget CSV row {rowStart} has invalid quotation.");
            }
            else
            {
                field.Append(c);
                started = true;
            }

            if (field.Length > 2_000)
            {
                throw new InvalidDataException($"Budget CSV row {rowStart} exceeds its field limit.");
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("The financial CSV ends inside a quoted field.");
        }

        return rows;
    }
}
