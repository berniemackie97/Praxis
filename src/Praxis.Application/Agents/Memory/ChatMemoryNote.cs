namespace Praxis.Application.Agents.Memory;

public sealed record ChatMemoryNote(Guid SourceId, string Title, string Digest, string Markdown);
