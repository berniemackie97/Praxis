using Praxis.Domain.Search;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>The result of a semantic index refresh.</summary>
public sealed record SemanticIndexResponse(SemanticIndexUpdate Index);
