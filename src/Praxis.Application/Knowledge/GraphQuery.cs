namespace Praxis.Application.Knowledge;

public sealed record GraphQuery(
    Guid? CollectionId = null, Guid? SourceId = null, string? EntityType = null,
    string? Predicate = null, string? EvidenceStatus = null,
    int Limit = 40, string? Cursor = null);
