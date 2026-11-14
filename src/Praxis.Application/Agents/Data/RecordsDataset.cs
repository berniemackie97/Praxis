using System.Globalization;
using Praxis.Application.Understanding;
using Praxis.Domain.Agents;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Agents.Data;

/// <summary>
/// Verified events, deadlines and contacts that intake found in current sources, each cited to the
/// exact quote that states it. Dates come from the document, never from a model's arithmetic.
/// </summary>
public sealed class RecordsDataset(IUnderstandingRepository understanding) : IDataset
{
    private const int MaximumRows = 20_000;

    public string Name => "records";

    public string Description =>
        "Events, deadlines and contacts stated in the owner's documents, verified against a printed quote. "
        + "date is an event's start or a deadline's due date; amount is minor units in currency.";

    public IReadOnlyList<DataField> Fields { get; } =
    [
        new("kind", DataField.Text, "event, deadline or contact", true),
        new("title", DataField.Text, "Event or deadline title", false),
        new("date", DataField.Date, "Event start or deadline due date", true),
        new("month", DataField.Text, "yyyy-MM of date", true),
        new("time", DataField.Text, "HH:mm start time when stated", false),
        new("end", DataField.Date, "Event end date when stated", false),
        new("location", DataField.Text, "Event location when stated", true),
        new("amount", DataField.Money, "Amount due in minor units when stated", false),
        new("currency", DataField.Text, "ISO currency of amount", true),
        new("name", DataField.Text, "Contact name", false),
        new("role", DataField.Text, "Contact role", true),
        new("organization", DataField.Text, "Contact organization", true),
        new("email", DataField.Text, "Contact email", false),
        new("phone", DataField.Text, "Contact phone", false),
        new("source", DataField.Text, "File the record was found in", true),
        new("tier", DataField.Text, "Model tier whose verified output produced it", true)
    ];

    public DatasetSnapshot Snapshot(AgentToolContext context)
    {
        IReadOnlyList<StoredRecord> all = understanding.ReadRecords(null, MaximumRows);
        List<DataRow> rows = new(all.Count);
        int withheld = 0;
        foreach (StoredRecord record in all)
        {
            if (!context.MayDisclose(record.SourceId))
            {
                withheld++;
                continue;
            }

            rows.Add(Row(record));
        }

        IReadOnlyList<string> gaps = all.Count == MaximumRows ? [$"Only the {MaximumRows} most recent records are available."] : [];
        return new DatasetSnapshot(rows, context.BackendIsLocal ? 0 : withheld, gaps);
    }

    private static DataRow Row(StoredRecord record)
    {
        string? moment = record.Kind switch
        {
            "event" => record.Fields.GetValueOrDefault("start"),
            "deadline" => record.Fields.GetValueOrDefault("due"),
            _ => null
        };
        DateOnly? date = Date(moment);
        Dictionary<string, object?> values = new(StringComparer.Ordinal)
        {
            ["kind"] = record.Kind,
            ["title"] = record.Fields.GetValueOrDefault("title"),
            ["date"] = date,
            ["month"] = date?.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            ["time"] = moment is { Length: >= 16 } ? moment[11..16] : null,
            ["end"] = Date(record.Fields.GetValueOrDefault("end")),
            ["location"] = record.Fields.GetValueOrDefault("location"),
            ["amount"] = long.TryParse(record.Fields.GetValueOrDefault("amount_minor"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long minor)
                ? minor
                : null,
            ["currency"] = record.Fields.GetValueOrDefault("currency"),
            ["name"] = record.Fields.GetValueOrDefault("name"),
            ["role"] = record.Fields.GetValueOrDefault("role"),
            ["organization"] = record.Fields.GetValueOrDefault("organization"),
            ["email"] = record.Fields.GetValueOrDefault("email"),
            ["phone"] = record.Fields.GetValueOrDefault("phone"),
            ["source"] = record.SourceName,
            ["tier"] = record.Tier
        };
        string locator = $"chars:{record.EvidenceOffset}-{record.EvidenceOffset + record.EvidenceLength}";
        return new DataRow(values, [new AgentCitation(record.SourceId, record.ItemId, record.Revision, locator, record.SourceName)]);
    }

    private static DateOnly? Date(string? moment) =>
        moment is { Length: >= 10 } && DateOnly.TryParseExact(moment[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date
            : null;
}
