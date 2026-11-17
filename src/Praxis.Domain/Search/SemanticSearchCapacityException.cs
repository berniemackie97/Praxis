namespace Praxis.Domain.Search;

/// <summary>The exact vector scan exceeded its supported corpus budget.</summary>
public sealed class SemanticSearchCapacityException() : InvalidOperationException("The semantic index exceeds the bounded exact-search budget.");
