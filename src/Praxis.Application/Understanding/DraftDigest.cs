using System.Text.Json;
using Praxis.Domain.Enrichment;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>
/// A verified draft rendered for the next tier: every item numbered (T topics, E entities, L
/// relations, D records) with its exact quote, so a reviewer can keep items by saying nothing,
/// drop them by id, and add only what is missing. The ids map back to the draft in code.
/// </summary>
public sealed class DraftDigest
{
    private readonly Dictionary<string, object> items = new(StringComparer.Ordinal);

    private DraftDigest(string json) => Json = json;

    public string Json { get; private set; }

    public IReadOnlyDictionary<string, object> Items => items;

    public static DraftDigest From(UnderstandingDraft draft, string text)
    {
        DraftDigest digest = new(string.Empty);
        List<object> topics = [];
        List<object> entities = [];
        List<object> relations = [];
        List<object> records = [];
        foreach (ProposalCandidate candidate in draft.Candidates)
        {
            string quote = text.Substring(candidate.EvidenceOffset, candidate.EvidenceLength);
            switch (candidate.Kind)
            {
                case "topic":
                    topics.Add(new { id = digest.Register("T", topics.Count, candidate), name = candidate.Value, quote });
                    break;
                case "entity":
                    entities.Add(new { id = digest.Register("E", entities.Count, candidate), name = candidate.Value, type = candidate.EntityType, quote });
                    break;
                case "relation":
                    relations.Add(new
                    {
                        id = digest.Register("L", relations.Count, candidate),
                        subject = candidate.RelationSubject,
                        predicate = candidate.Value,
                        @object = candidate.RelationObject,
                        quote
                    });
                    break;
            }
        }

        foreach (IntakeRecord record in draft.Records)
        {
            Dictionary<string, object> fields = new(StringComparer.Ordinal)
            {
                ["id"] = digest.Register("D", records.Count, record),
                ["kind"] = record.Kind,
                ["quote"] = text.Substring(record.EvidenceOffset, record.EvidenceLength)
            };
            foreach ((string key, string value) in record.Fields)
            {
                fields[key] = value;
            }

            records.Add(fields);
        }

        digest.Json = JsonSerializer.Serialize(new { summary = draft.Summary, documentType = draft.DocumentType, topics, entities, relations, records });
        return digest;
    }

    private string Register(string prefix, int index, object item)
    {
        string id = prefix + (index + 1);
        items[id] = item;
        return id;
    }
}
