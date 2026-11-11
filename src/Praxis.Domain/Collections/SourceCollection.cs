namespace Praxis.Domain.Collections;

public sealed record SourceCollection(Guid CollectionId, string Name, int ActiveSourceCount);
