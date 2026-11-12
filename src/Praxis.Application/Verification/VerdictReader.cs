using System.Text.Json;
using Praxis.Application.Inference;
using Praxis.Domain.Knowledge;
using Praxis.Domain.Verification;

namespace Praxis.Application.Verification;

/// <summary>Validates a verifier's answer against the ordinals that were actually sent.</summary>
public static class VerdictReader
{
    public static IReadOnlyList<VerificationVerdict> Read(string json, IReadOnlySet<int> sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        if (string.IsNullOrWhiteSpace(json) || sent.Count == 0 ||
            System.Text.Encoding.UTF8.GetByteCount(json) > 64 * 1024)
        {
            throw Invalid();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("verdicts", out var verdicts) ||
                verdicts.ValueKind != JsonValueKind.Array)
            {
                throw Invalid();
            }

            var result = new Dictionary<int, VerificationVerdict>();
            foreach (var item in verdicts.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("ordinal", out var ordinalValue) ||
                    ordinalValue.ValueKind != JsonValueKind.Number ||
                    !ordinalValue.TryGetInt32(out var ordinal))
                {
                    throw Invalid();
                }

                if (!sent.Contains(ordinal))
                {
                    continue;
                }

                string? verdict = Text(item, "verdict");
                string? corrected = Text(item, "correctedType");
                string? note = Text(item, "note");
                if (verdict is not ("supported" or "unsupported" or "uncertain") ||
                    corrected is null || note is null || result.ContainsKey(ordinal) ||
                    (corrected != VerificationPrompt.NoTypeChange && !KnowledgeVocabulary.EntityTypes.Contains(corrected)))
                {
                    throw Invalid();
                }

                note = note.Trim();
                result.Add(ordinal, new VerificationVerdict(ordinal, verdict,
                    corrected == VerificationPrompt.NoTypeChange ? null : corrected,
                    note.Length == 0 ? null : note.Length <= 300 ? note : note[..300]));
            }

            if (result.Count != sent.Count)
            {
                throw Invalid();
            }

            return result.Values.OrderBy(verdict => verdict.Ordinal).ToArray();
        }
        catch (JsonException exception)
        {
            throw new InferenceException(InferenceFailure.InvalidResponse,
                "The verifier returned malformed JSON.", exception);
        }
    }

    private static string? Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static InferenceException Invalid() =>
        new(InferenceFailure.InvalidResponse, "The verifier response does not match the schema.");
}
