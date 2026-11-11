using Praxis.Domain.Knowledge;

namespace Praxis.Domain.Enrichment;

/// <summary>Validation of proposals before they are recorded and of the owner's review decisions.</summary>
public static class ProposalRules
{
    /// <summary>
    /// Rejects runs with too many proposals, unknown kinds, entity types or predicates, malformed
    /// names, unsupported flags, or evidence spans outside <paramref name="content"/>.
    /// </summary>
    public static void ValidateCandidates(string content, IReadOnlyList<ProposalCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count > ProposalLimits.MaximumPerRun)
        {
            throw new ArgumentException("A run may record at most 64 proposals.", nameof(candidates));
        }

        foreach (ProposalCandidate candidate in candidates)
        {
            bool kindValid = candidate.Kind switch
            {
                "topic" => candidate.EntityType is null && candidate.RelationSubject is null,
                "entity" => candidate.EntityType is not null &&
                    KnowledgeVocabulary.EntityTypes.Contains(candidate.EntityType) &&
                    candidate.RelationSubject is null,
                "relation" => candidate.EntityType is null &&
                    KnowledgeVocabulary.Predicates.Contains(candidate.Value) &&
                    IsValidName(candidate.RelationSubject) && IsValidName(candidate.RelationObject),
                _ => false
            };
            kindValid = kindValid && (candidate.Flags is null or InstructionLikeText.Flag) &&
                (candidate.Kind == "relation") == (candidate.RelationObject is not null);
            if (!kindValid || candidate.Value.Length is < 1 or > ProposalLimits.MaximumValueLength ||
                candidate.Value.Any(char.IsControl) ||
                candidate.EvidenceLength is < 1 or > ProposalLimits.MaximumEvidenceLength ||
                candidate.EvidenceOffset < 0 ||
                candidate.EvidenceOffset > content.Length - candidate.EvidenceLength)
            {
                throw new ArgumentException("Each proposal needs a valid kind, value and evidence span.",
                    nameof(candidates));
            }
        }
    }

    /// <summary>
    /// Validates an owner review: the decision is accepted, rejected or corrected, and only a
    /// correction carries printable replacement text. Returns the trimmed correction.
    /// </summary>
    public static string? NormalizeReview(string decision, string? correction)
    {
        if (decision is not ("accepted" or "rejected" or "corrected"))
        {
            throw new ArgumentException("The review decision is invalid.", nameof(decision));
        }

        if (decision != "corrected")
        {
            return correction is null
                ? null
                : throw new ArgumentException("Only corrected proposals accept replacement text.", nameof(correction));
        }

        string? trimmed = correction?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length > ProposalLimits.MaximumValueLength ||
            trimmed.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Corrections must be printable and at most 120 characters.", nameof(correction));
        }

        return trimmed;
    }

    private static bool IsValidName(string? name) =>
        name is { Length: >= 1 and <= ProposalLimits.MaximumValueLength } && !name.Any(char.IsControl);
}
