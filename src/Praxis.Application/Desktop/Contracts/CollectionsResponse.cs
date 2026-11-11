using Praxis.Domain.Collections;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>A page of owner-authored collections.</summary>
public sealed record CollectionsResponse(IReadOnlyList<SourceCollection> Collections);
