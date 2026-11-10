using System.Text.Json;
using Praxis.Inference;
using Praxis.Storage.Knowledge;

namespace Praxis.Storage.Verification;

internal sealed record VerificationVerdict(int Ordinal, string Verdict, string? CorrectedType, string? Note);

/// <summary>Validates a verifier's answer against the ordinals that were actually sent.</summary>
internal static class VerdictReader
{
    internal static IReadOnlyList<VerificationVerdict> Read(string json, IReadOnlySet<int> sent)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("verdicts", out var verdicts) ||
                verdicts.ValueKind != JsonValueKind.Array)
            {
                throw Invalid();
            }

            var result = new Dictionary<int, VerificationVerdict>();
            foreach (var item in verdicts.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("ordinal", out var ordinalValue) || !ordinalValue.TryGetInt32(out var ordinal) ||
                    !sent.Contains(ordinal) || result.ContainsKey(ordinal) ||
                    Text(item, "verdict") is not ("supported" or "unsupported" or "uncertain") ||
                    Text(item, "correctedType") is not { } corrected ||
                    (corrected != VerificationPrompt.NoTypeChange && !KnowledgeVocabulary.EntityTypes.Contains(corrected)))
                {
                    continue; // Answers about items that were not sent, or malformed entries, are ignored.
                }

                var note = Text(item, "note")?.Trim();
                result[ordinal] = new VerificationVerdict(ordinal, Text(item, "verdict")!,
                    corrected == VerificationPrompt.NoTypeChange ? null : corrected,
                    string.IsNullOrEmpty(note) ? null : note.Length <= 300 ? note : note[..300]);
            }

            return result.Values.OrderBy(verdict => verdict.Ordinal).ToArray();
        }
        catch (JsonException exception)
        {
            throw new InferenceException(InferenceFailure.InvalidResponse, "The verifier returned malformed JSON.", exception);
        }
    }

    private static string? Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static InferenceException Invalid() =>
        new(InferenceFailure.InvalidResponse, "The verifier response does not match the schema.");
}
