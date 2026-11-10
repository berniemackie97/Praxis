using System.Text.Json;
using Praxis.Inference;
using Praxis.Storage.Enrichment;
using Praxis.Storage.Knowledge;

namespace Praxis.Storage.Enrichment.Semantic;

/// <summary>
/// Converts a model's structured answer for one segment into proposal candidates. An item is
/// kept only when its quote is found in the segment; an entity's name must also appear in the
/// segment; a relation must join two entities accepted from the same answer, use a known
/// predicate and quote both names. Evidence that reads like an instruction is flagged.
/// Everything else is counted as rejected; model output is never trusted as fact.
/// </summary>
internal static class SemanticResponseReader
{
    internal static (IReadOnlyList<ProposalCandidate> Accepted, int Rejected) Read(
        string json, string segmentText, int segmentStart)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InferenceException(InferenceFailure.InvalidResponse,
                "The model returned malformed JSON.", exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryArray(root, "topics", out JsonElement topics) || !TryArray(root, "entities", out JsonElement entities))
            {
                throw new InferenceException(InferenceFailure.InvalidResponse,
                    "The model response does not match the extraction schema.");
            }

            Reading reader = new Reading(segmentText, segmentStart);
            foreach (JsonElement topic in Limit(topics, SemanticExtractionPrompt.MaximumTopics, reader))
            {
                reader.AddNamed(topic, "topic", null);
            }

            foreach (JsonElement entity in Limit(entities, SemanticExtractionPrompt.MaximumEntities, reader))
            {
                string? type = Text(entity, "type");
                if (type is null || !KnowledgeVocabulary.EntityTypes.Contains(type))
                {
                    reader.Rejected++;
                    continue;
                }

                reader.AddNamed(entity, "entity", type);
            }

            if (TryArray(root, "relations", out JsonElement relations))
            {
                foreach (JsonElement relation in Limit(relations, SemanticExtractionPrompt.MaximumRelations, reader))
                {
                    reader.AddRelation(relation);
                }
            }

            return (reader.Accepted, reader.Rejected);
        }
    }

    private static IEnumerable<JsonElement> Limit(JsonElement array, int maximum, Reading reader)
    {
        reader.Rejected += Math.Max(0, array.GetArrayLength() - maximum);
        return array.EnumerateArray().Take(maximum);
    }

    private static bool TryArray(JsonElement root, string property, out JsonElement array) =>
        root.TryGetProperty(property, out array) && array.ValueKind == JsonValueKind.Array;

    private static string? Text(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Normalize(string? name)
    {
        if (name is null)
        {
            return null;
        }

        string collapsed = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length is >= 1 and <= KnowledgeVocabulary.MaximumNameLength &&
            collapsed.Any(char.IsLetterOrDigit) && !collapsed.Any(char.IsControl)
            ? collapsed : null;
    }

    private sealed class Reading(string segmentText, int segmentStart)
    {
        private readonly Dictionary<string, string> entityNames = new(StringComparer.Ordinal);

        public List<ProposalCandidate> Accepted { get; } = [];
        public int Rejected { get; set; }

        public void AddNamed(JsonElement item, string kind, string? entityType)
        {
            string? name = Normalize(Text(item, "name"));
            (int Offset, int Length)? span = Locate(Text(item, "quote"));
            if (name is null || span is not { } found ||
                (kind == "entity" && !segmentText.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                Rejected++;
                return;
            }

            if (kind == "entity" && KnowledgeVocabulary.NameKey(name) is { } key)
            {
                entityNames.TryAdd(key, name);
            }

            Accepted.Add(new ProposalCandidate(kind, entityType, name,
                segmentStart + found.Offset, found.Length, Flags: Flag(found)));
        }

        public void AddRelation(JsonElement item)
        {
            string? predicate = Text(item, "predicate");
            string? subject = Resolve(Text(item, "subject"));
            string? obj = Resolve(Text(item, "object"));
            (int Offset, int Length)? span = Locate(Text(item, "quote"));
            if (predicate is null || !KnowledgeVocabulary.Predicates.Contains(predicate) ||
                subject is null || obj is null || span is not { } found ||
                KnowledgeVocabulary.NameKey(subject) == KnowledgeVocabulary.NameKey(obj))
            {
                Rejected++;
                return;
            }

            ReadOnlySpan<char> evidence = segmentText.AsSpan(found.Offset, found.Length);
            if (!evidence.Contains(subject, StringComparison.OrdinalIgnoreCase) ||
                !evidence.Contains(obj, StringComparison.OrdinalIgnoreCase))
            {
                Rejected++;
                return;
            }

            Accepted.Add(new ProposalCandidate("relation", null, predicate,
                segmentStart + found.Offset, found.Length, subject, obj, Flag(found)));
        }

        /// <summary>A relation end must be an entity accepted from this same answer.</summary>
        private string? Resolve(string? name) =>
            KnowledgeVocabulary.NameKey(Normalize(name)) is { } key &&
            entityNames.TryGetValue(key, out string? accepted) ? accepted : null;

        private (int Offset, int Length)? Locate(string? quote) =>
            quote is not null && EvidenceLocator.Find(segmentText, quote) is { } found &&
            found.Length <= LocalProposals.MaximumEvidenceLength ? found : null;

        private string? Flag((int Offset, int Length) span) =>
            InstructionLikeText.Matches(segmentText, span.Offset, span.Length) ? InstructionLikeText.Flag : null;
    }
}
