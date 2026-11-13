using System.Text.Json;
using Praxis.Application.Understanding.Profiles;
using Praxis.Domain.Enrichment;
using Praxis.Domain.Knowledge;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>
/// Parses a model's understanding and keeps only what code can verify in the document: each quote
/// must be found in the span the model read; entity and relation names must occur in their quote;
/// records must pass their profile. Instruction-like evidence is flagged. Nothing is trusted on the
/// model's word.
/// </summary>
public static class UnderstandingVerifier
{
    public static UnderstandingDraft? Check(string content, string text, DocumentChunk chunk, RecordProfiles profiles,
        IReadOnlyCollection<string> requestedKinds, string tier, string? providerId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 6 });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            List<ProposalCandidate> candidates = [];
            List<IntakeRecord> records = [];
            int verified = 0;
            int rejected = 0;

            void Count(bool ok)
            {
                if (ok)
                {
                    verified++;
                }
                else
                {
                    rejected++;
                }
            }

            foreach (JsonElement topic in Items(root, "topics").Take(8))
            {
                Count(Evidence(topic, text, chunk) is { } span && Name(topic, "name") is { } name &&
                      Add(candidates, new ProposalCandidate("topic", null, name, span.Offset, span.Length), text));
            }

            foreach (JsonElement entity in Items(root, "entities"))
            {
                string? type = Name(entity, "type");
                Count(Evidence(entity, text, chunk) is { } span && Name(entity, "name") is { } name && type is not null &&
                      KnowledgeVocabulary.EntityTypes.Contains(type) && EvidenceLocator.Mentions(text.Substring(span.Offset, span.Length), name) &&
                      Add(candidates, new ProposalCandidate("entity", type, name, span.Offset, span.Length), text));
            }

            foreach (JsonElement relation in Items(root, "relations"))
            {
                string? predicate = Name(relation, "predicate");
                Count(Evidence(relation, text, chunk) is { } span && Name(relation, "subject") is { } subject && Name(relation, "object") is { } target &&
                      predicate is not null && KnowledgeVocabulary.Predicates.Contains(predicate) &&
                      EvidenceLocator.Mentions(text.Substring(span.Offset, span.Length), subject) &&
                      EvidenceLocator.Mentions(text.Substring(span.Offset, span.Length), target) &&
                      Add(candidates, new ProposalCandidate("relation", null, predicate, span.Offset, span.Length, subject, target), text));
            }

            foreach (JsonElement record in Items(root, "records").Take(IntakeRecord.MaximumRecordsPerDocument))
            {
                string? kind = Name(record, "kind");
                IRecordProfile? profile = kind is not null && requestedKinds.Contains(kind) ? profiles.For(kind) : null;
                (int Offset, int Length)? span = Evidence(record, text, chunk);
                IReadOnlyDictionary<string, string>? fields = profile is null || span is null
                    ? null
                    : profile.Normalize(Fields(record), text.Substring(span.Value.Offset, span.Value.Length));
                if (fields is not null)
                {
                    records.Add(new IntakeRecord(kind!, fields, span!.Value.Offset, span.Value.Length, tier, providerId, profile!.SortKey(fields)));
                }

                Count(fields is not null);
            }

            string? summary = Name(root, "summary", UnderstandingCommit.MaximumSummaryLength);
            return new UnderstandingDraft(summary, Name(root, "documentType", DocumentProfile.MaximumTypeLength), candidates, records, verified, rejected);
        }
    }

    /// <summary>Reads the combined summary of a long document.</summary>
    public static string? Summary(string content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 3 });
            return document.RootElement.ValueKind == JsonValueKind.Object ? Name(document.RootElement, "summary", UnderstandingCommit.MaximumSummaryLength) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Add(List<ProposalCandidate> candidates, ProposalCandidate candidate, string text)
    {
        ProposalCandidate flagged = InstructionLikeText.Matches(text, candidate.EvidenceOffset, candidate.EvidenceLength)
            ? candidate with { Flags = InstructionLikeText.Flag }
            : candidate;
        candidates.Add(flagged);
        return true;
    }

    private static (int Offset, int Length)? Evidence(JsonElement item, string text, DocumentChunk chunk) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("quote", out JsonElement quote) && quote.ValueKind == JsonValueKind.String
            ? EvidenceLocator.Find(text, chunk.Offset, chunk.Length, quote.GetString())
            : null;

    private static IEnumerable<JsonElement> Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

    private static string? Name(JsonElement item, string name, int maximum = ProposalLimits.MaximumValueLength) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        value.GetString()!.Trim() is { Length: > 0 } text && text.Length <= maximum && !text.Any(char.IsControl)
            ? text
            : null;

    private static Dictionary<string, string> Fields(JsonElement record)
    {
        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (JsonProperty property in record.EnumerateObject())
        {
            if (property.Name is not ("kind" or "quote") && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                fields[property.Name] = property.Value.ValueKind == JsonValueKind.Number ? property.Value.GetRawText() : property.Value.GetString()!;
            }
        }

        return fields;
    }
}
