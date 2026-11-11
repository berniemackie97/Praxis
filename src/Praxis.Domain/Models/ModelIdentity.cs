namespace Praxis.Domain.Models;

/// <summary>Identity recorded with every model-produced result for provenance and cache keys.</summary>
public sealed record ModelIdentity(string Provider, string Model, string Digest, string? Family, string? ParameterSize, string? Quantization);
