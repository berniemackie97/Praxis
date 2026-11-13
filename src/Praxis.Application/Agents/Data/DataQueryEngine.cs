using System.Globalization;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Data;

/// <summary>
/// Executes filter, group, metric, sort and page over a dataset snapshot with exact arithmetic.
/// Money is summed as integer minor units and always grouped by currency, so totals never mix
/// currencies. Averages are reported as exact decimals rounded half-even to two places of the
/// minor unit and labelled as averages.
/// </summary>
public static class DataQueryEngine
{
    public const int MaximumRows = 200;

    public static object Run(IDataset dataset, DatasetSnapshot snapshot, IReadOnlyList<DataFilter> filters, IReadOnlyList<string> groupBy,
        IReadOnlyList<DataMetric> metrics, string? sort, bool descending, int limit, int offset, out IReadOnlyList<AgentCitation> citations)
    {
        Dictionary<string, DataField> fields = dataset.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        foreach (DataFilter filter in filters)
        {
            Require(fields, filter.Field);
            if (!DataFilter.Operators.Contains(filter.Op, StringComparer.Ordinal) || filter.Values.Count == 0 || filter.Values.Count > 50)
            {
                throw new ArgumentException($"Filter on '{filter.Field}' needs an operator ({string.Join(", ", DataFilter.Operators)}) and 1–50 values.");
            }
        }

        foreach (string field in groupBy)
        {
            if (!Require(fields, field).Groupable)
            {
                throw new ArgumentException($"'{field}' cannot be grouped. Groupable: {string.Join(", ", dataset.Fields.Where(item => item.Groupable).Select(item => item.Name))}.");
            }
        }

        foreach (DataMetric metric in metrics)
        {
            if (!DataMetric.Functions.Contains(metric.Function, StringComparer.Ordinal) || (metric.Function != "count" && metric.Field is null))
            {
                throw new ArgumentException("Metrics are count, or sum/min/max/avg of a field.");
            }

            if (metric.Field is not null && Require(fields, metric.Field).Type is not (DataField.Money or DataField.Integer) && metric.Function is "sum" or "avg")
            {
                throw new ArgumentException($"'{metric.Function}' needs a money or integer field.");
            }
        }

        DataRow[] rows = snapshot.Rows.Where(row => filters.All(filter => Matches(row, fields[filter.Field], filter))).ToArray();
        if (groupBy.Count == 0 && metrics.Count == 0)
        {
            IEnumerable<DataRow> ordered = sort is null ? rows : Order(rows, row => row.Values.GetValueOrDefault(Require(fields, sort).Name), descending);
            DataRow[] page = ordered.Skip(offset).Take(Math.Min(limit, MaximumRows)).ToArray();
            citations = page.SelectMany(row => row.Evidence).DistinctBy(citation => (citation.ItemId, citation.Locator)).Take(60).ToArray();
            return new
            {
                dataset = dataset.Name,
                matched = rows.Length,
                rows = page.Select(row => row.Values),
                nextOffset = offset + page.Length < rows.Length ? offset + page.Length : (int?)null
            };
        }

        bool money = metrics.Any(metric => metric.Field is not null && fields[metric.Field].Type == DataField.Money);
        List<string> keys = [.. groupBy];
        if (money && fields.ContainsKey("currency") && !keys.Contains("currency"))
        {
            keys.Add("currency");
        }

        IReadOnlyList<DataMetric> measures = metrics.Count == 0 ? [new DataMetric("count", null)] : metrics;
        List<Dictionary<string, object?>> groups = rows
            .GroupBy(row => string.Join("\u001f", keys.Select(key => Format(row.Values.GetValueOrDefault(key)))))
            .Select(group =>
            {
                Dictionary<string, object?> result = new(StringComparer.Ordinal);
                DataRow first = group.First();
                foreach (string key in keys)
                {
                    result[key] = first.Values.GetValueOrDefault(key);
                }

                foreach (DataMetric metric in measures)
                {
                    result[metric.Name] = Measure(group, metric);
                }

                return result;
            }).ToList();
        string order = sort ?? measures[0].Name;
        IEnumerable<Dictionary<string, object?>> sorted = Order(groups, group => group.GetValueOrDefault(order), sort is null || descending);
        Dictionary<string, object?>[] pageGroups = sorted.Skip(offset).Take(Math.Min(limit, MaximumRows)).ToArray();
        citations = [];
        return new
        {
            dataset = dataset.Name,
            matchedRows = rows.Length,
            groups = pageGroups,
            nextOffset = offset + pageGroups.Length < groups.Count ? offset + pageGroups.Length : (int?)null,
            note = money ? "Money values are integer minor units (cents); totals are per currency." : null
        };
    }

    private static DataField Require(IReadOnlyDictionary<string, DataField> fields, string name) =>
        fields.TryGetValue(name, out DataField? field) ? field : throw new ArgumentException($"Unknown field '{name}'. Fields: {string.Join(", ", fields.Keys)}.");

    private static object? Measure(IEnumerable<DataRow> rows, DataMetric metric)
    {
        if (metric.Function == "count")
        {
            return rows.Count();
        }

        object?[] values = rows.Select(row => row.Values.GetValueOrDefault(metric.Field!)).Where(value => value is not null).ToArray();
        return metric.Function switch
        {
            "sum" => values.Aggregate(0L, (sum, value) => checked(sum + Convert.ToInt64(value, CultureInfo.InvariantCulture))),
            "avg" => values.Length == 0 ? null
                : Math.Round(values.Sum(value => Convert.ToDecimal(value, CultureInfo.InvariantCulture)) / values.Length, 2, MidpointRounding.ToEven),
            "min" => values.Order(ValueComparer.Instance).FirstOrDefault(),
            _ => values.Order(ValueComparer.Instance).LastOrDefault()
        };
    }

    private static bool Matches(DataRow row, DataField field, DataFilter filter)
    {
        object? value = row.Values.GetValueOrDefault(field.Name);
        object?[] targets = filter.Values.Select(text => Parse(field, text)).ToArray();
        int Compare(object? target) => ValueComparer.Instance.Compare(value, target);
        return filter.Op switch
        {
            "eq" => Compare(targets[0]) == 0,
            "ne" => Compare(targets[0]) != 0,
            "in" => targets.Any(target => Compare(target) == 0),
            "gt" => value is not null && Compare(targets[0]) > 0,
            "gte" => value is not null && Compare(targets[0]) >= 0,
            "lt" => value is not null && Compare(targets[0]) < 0,
            "lte" => value is not null && Compare(targets[0]) <= 0,
            "between" => targets.Length == 2 && value is not null && Compare(targets[0]) >= 0 && Compare(targets[1]) <= 0,
            _ => Format(value).Contains(filter.Values[0], StringComparison.OrdinalIgnoreCase)
        };
    }

    private static object? Parse(DataField field, string text) => field.Type switch
    {
        DataField.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date : throw new ArgumentException($"'{field.Name}' values must be yyyy-MM-dd dates."),
        DataField.Money or DataField.Integer => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number)
            ? number : throw new ArgumentException($"'{field.Name}' values must be integers (money in minor units, e.g. cents)."),
        _ => text
    };

    private static string Format(object? value) => value switch
    {
        null => "",
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static IEnumerable<T> Order<T>(IEnumerable<T> items, Func<T, object?> key, bool descending) =>
        descending ? items.OrderByDescending(key, ValueComparer.Instance) : items.OrderBy(key, ValueComparer.Instance);

    private sealed class ValueComparer : IComparer<object?>
    {
        public static readonly ValueComparer Instance = new();

        public int Compare(object? left, object? right) => (left, right) switch
        {
            (null, null) => 0,
            (null, _) => -1,
            (_, null) => 1,
            (DateOnly a, DateOnly b) => a.CompareTo(b),
            (string a, string b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase),
            _ when IsNumber(left) && IsNumber(right) => Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture)),
            _ => string.Compare(Format(left), Format(right), StringComparison.OrdinalIgnoreCase)
        };

        private static bool IsNumber(object value) => value is long or int or decimal or double;
    }
}
