namespace Praxis.Domain.Search;

/// <summary>A current, active text segment awaiting an embedding, bound to its source digest.</summary>
public sealed record SemanticSegment(Guid ItemId, int Revision, int Ordinal, string SourceDigest, string Text);
