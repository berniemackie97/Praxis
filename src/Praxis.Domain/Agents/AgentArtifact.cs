namespace Praxis.Domain.Agents;

/// <summary>A document the agent authored (budget, report, plan), stored as a captured Markdown source with revisions.</summary>
public sealed record AgentArtifact(Guid ArtifactId, string Title, Guid SourceId, int Revision, string Path, Guid? ConversationId, DateTimeOffset UpdatedUtc);
