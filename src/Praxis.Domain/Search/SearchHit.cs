namespace Praxis.Domain.Search;

public sealed record SearchHit(Guid SourceId, Guid ItemId, int Revision, string Title, string SourceLocator, string OriginalSha256, string Snippet);
