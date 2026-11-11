namespace Praxis.Domain.Models;

/// <summary>Owner-selected local model for one processing role.</summary>
public sealed record ModelProfile(
    string Role, string Provider, string Endpoint, string Model, int ContextTokens, bool Enabled)
{
    public const string LocalEnrichment = "local-enrichment";
    public const string LocalEmbedding = "local-embedding";
}
