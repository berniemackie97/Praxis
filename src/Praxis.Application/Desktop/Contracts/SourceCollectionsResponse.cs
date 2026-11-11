namespace Praxis.Application.Desktop.Contracts;

/// <summary>The collections that contain a source.</summary>
public sealed record SourceCollectionsResponse(IReadOnlyList<Guid> CollectionIds);
