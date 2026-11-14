using System.Text.Json;
using Praxis.Application.Understanding.Profiles;
using Praxis.Domain.Enrichment;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>
/// Applies a reviewing tier's delta to the draft it reviewed: items it removed by id are dropped,
/// items it added are verified exactly like a full answer, and everything else is kept as is. The
/// reviewer pays only for its corrections, not for repeating what was already right.
/// </summary>
public static class UnderstandingDelta
{
    public static (UnderstandingDraft Draft, CascadeStep Step, bool Acceptable)? Apply(
        string content, UnderstandingDraft previous, DraftDigest digest, string text, DocumentChunk chunk, RecordProfiles profiles,
        IReadOnlyCollection<string> requestedKinds, string tier, string? providerId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 7 });
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

            HashSet<object> removed = new(ReferenceEqualityComparer.Instance);
            if (root.TryGetProperty("remove", out JsonElement remove) && remove.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement id in remove.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.String && digest.Items.TryGetValue(id.GetString()!, out object? item))
                    {
                        removed.Add(item);
                    }
                }
            }

            // Additions are checked by the same verifier as a full answer. A reviewer that answers in the
            // full shape instead of a delta has its items treated as additions.
            JsonElement add = root.TryGetProperty("add", out JsonElement additions) && additions.ValueKind == JsonValueKind.Object ? additions : root;
            string summary = root.TryGetProperty("summary", out JsonElement s) && s.ValueKind == JsonValueKind.String && s.GetString()!.Trim().Length > 0
                ? s.GetString()!
                : previous.Summary ?? string.Empty;
            string addedJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["summary"] = summary,
                ["documentType"] = root.TryGetProperty("documentType", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : previous.DocumentType,
                ["topics"] = add.ValueKind == JsonValueKind.Object && add.TryGetProperty("topics", out JsonElement topics) ? topics : null,
                ["entities"] = add.ValueKind == JsonValueKind.Object && add.TryGetProperty("entities", out JsonElement entities) ? entities : null,
                ["relations"] = add.ValueKind == JsonValueKind.Object && add.TryGetProperty("relations", out JsonElement relations) ? relations : null,
                ["records"] = add.ValueKind == JsonValueKind.Object && add.TryGetProperty("records", out JsonElement records) ? records : null
            });
            UnderstandingDraft? added = UnderstandingVerifier.Check(addedJson, text, chunk, profiles, requestedKinds, tier, providerId);
            if (added is null)
            {
                return null;
            }

            ProposalCandidate[] keptCandidates = previous.Candidates.Where(candidate => !removed.Contains(candidate)).ToArray();
            IntakeRecord[] keptRecords = previous.Records.Where(record => !removed.Contains(record)).ToArray();
            // Items the reviewer repeats are not additions.
            HashSet<string> known = keptCandidates.Select(DocumentUnderstander.CandidateKey).ToHashSet(StringComparer.Ordinal);
            HashSet<string> knownRecords = keptRecords.Select(RecordKey).ToHashSet(StringComparer.Ordinal);
            ProposalCandidate[] newCandidates = added.Candidates.Where(candidate => known.Add(DocumentUnderstander.CandidateKey(candidate))).ToArray();
            IntakeRecord[] newRecords = added.Records.Where(record => knownRecords.Add(RecordKey(record))).ToArray();
            int kept = keptCandidates.Length + keptRecords.Length;
            int addedCount = newCandidates.Length + newRecords.Length;
            UnderstandingDraft merged = new(
                added.Summary ?? previous.Summary, added.DocumentType ?? previous.DocumentType,
                DocumentUnderstander.Merge(keptCandidates.Concat(newCandidates)),
                keptRecords.Concat(newRecords).ToArray(),
                kept + addedCount, added.Rejected);
            // The review is acceptable when its own additions pass verification; an empty correction approves the draft.
            bool acceptable = merged.Summary is not null && added.Rejected <= (addedCount + added.Rejected) * UnderstandingDraft.TolerableRejection;
            return (merged, new CascadeStep(tier, providerId, kept, removed.Count, addedCount, added.Rejected, 0, 0), acceptable);
        }
    }

    private static string RecordKey(IntakeRecord record) =>
        record.Kind + "\u001f" + string.Join("\u001f", record.Fields.OrderBy(field => field.Key, StringComparer.Ordinal).Select(field => field.Key + "=" + field.Value));
}
