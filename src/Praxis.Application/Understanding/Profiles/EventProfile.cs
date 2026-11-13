using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding.Profiles;

/// <summary>Meetings, appointments, trips and other dated happenings.</summary>
public sealed class EventProfile : IRecordProfile
{
    public string Kind => RecordKinds.Event;

    public string PromptFields => "event: title, start (YYYY-MM-DD or YYYY-MM-DDTHH:MM), optional end, optional location";

    public IReadOnlyDictionary<string, string>? Normalize(IReadOnlyDictionary<string, string> fields, string evidence)
    {
        string? title = RecordFields.Text(fields, "title");
        string? start = RecordFields.VerifiedMoment(RecordFields.Text(fields, "start"), evidence);
        if (title is null || start is null)
        {
            return null;
        }

        Dictionary<string, string> normalized = new(StringComparer.Ordinal) { ["title"] = title, ["start"] = start };
        if (RecordFields.VerifiedMoment(RecordFields.Text(fields, "end"), evidence) is { } end && string.CompareOrdinal(end, start) >= 0)
        {
            normalized["end"] = end;
        }

        if (RecordFields.Text(fields, "location") is { } location && EvidenceLocator.Mentions(evidence, location))
        {
            normalized["location"] = location;
        }

        return normalized;
    }

    public string? SortKey(IReadOnlyDictionary<string, string> normalized) => normalized.GetValueOrDefault("start");
}
