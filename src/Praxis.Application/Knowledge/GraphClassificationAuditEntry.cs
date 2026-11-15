namespace Praxis.Application.Knowledge;

/// <summary>Counts concern project-tagged mentions, not independent project boundaries.
/// A supported proposal may have received a type correction; TypeOrigin describes the effective type separately.</summary>
public sealed record GraphClassificationAuditEntry(GraphNode Entity, int AcceptedProjectMentions,
    int VerifierSupportedProjectMentions, int TechnicalProjectMentions, int UnverifiedDerivedProjectMentions);
