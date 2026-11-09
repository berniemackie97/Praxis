using System.Text.Json;
using Praxis.Inference;

namespace Praxis.Storage;

/// <summary>
/// Converts a model's structured answer for one segment into proposal candidates, keeping only
/// items whose quote is found in that segment and, for entities, whose name appears in it.
/// Everything else is counted as rejected; the model's output is never trusted as fact.
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
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("topics", out var topics) || topics.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Array)
            {
                throw new InferenceException(InferenceFailure.InvalidResponse,
                    "The model response does not match the extraction schema.");
            }

            var accepted = new List<ProposalCandidate>();
            var rejected = 0;
            foreach (var topic in topics.EnumerateArray().Take(SemanticExtractionPrompt.MaximumTopics))
            {
                Add(topic, "topic", null);
            }

            foreach (var entity in entities.EnumerateArray().Take(SemanticExtractionPrompt.MaximumEntities))
            {
                var type = String(entity, "type");
                if (type is null || !LocalProposals.EntityTypes.Contains(type))
                {
                    rejected++;
                    continue;
                }

                Add(entity, "entity", type);
            }

            rejected += Math.Max(0, topics.GetArrayLength() - SemanticExtractionPrompt.MaximumTopics) +
                Math.Max(0, entities.GetArrayLength() - SemanticExtractionPrompt.MaximumEntities);
            return (accepted, rejected);

            void Add(JsonElement item, string kind, string? entityType)
            {
                var name = Normalize(String(item, "name"));
                var quote = String(item, "quote");
                var span = quote is null ? null : EvidenceLocator.Find(segmentText, quote);
                if (name is null || span is not { } found ||
                    found.Length > LocalProposals.MaximumEvidenceLength ||
                    (kind == "entity" && !segmentText.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    rejected++;
                    return;
                }

                accepted.Add(new ProposalCandidate(kind, entityType, name,
                    segmentStart + found.Offset, found.Length));
            }
        }
    }

    private static string? String(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Normalize(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var collapsed = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length is >= 1 and <= LocalProposals.MaximumValueLength &&
            collapsed.Any(char.IsLetterOrDigit) && !collapsed.Any(char.IsControl)
            ? collapsed : null;
    }
}
