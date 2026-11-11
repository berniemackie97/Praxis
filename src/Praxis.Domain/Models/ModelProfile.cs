namespace Praxis.Domain.Models;

/// <summary>Owner-selected local model for one processing role.</summary>
public sealed record ModelProfile(
    string Role, string Provider, string Endpoint, string Model, int ContextTokens, bool Enabled)
{
    public const string LocalEnrichment = "local-enrichment";
    public const string LocalEmbedding = "local-embedding";
    public const string OllamaProvider = "ollama";

    public static void ValidateRole(string role)
    {
        if (role is not (LocalEnrichment or LocalEmbedding))
        {
            throw new ArgumentException("The model role is not supported.", nameof(role));
        }
    }

    /// <summary>
    /// The profile with a canonical loopback endpoint and trimmed model name. Rejects unsupported
    /// roles or providers, non-loopback endpoints, malformed model names and out-of-range context.
    /// </summary>
    public ModelProfile Normalized()
    {
        ValidateRole(Role);
        if (Provider != OllamaProvider)
        {
            throw new ArgumentException("The model provider is not supported.", nameof(Provider));
        }

        string endpoint = LoopbackEndpoint.Validate(Endpoint).GetLeftPart(UriPartial.Authority) + "/";
        string model = Model?.Trim() ?? "";
        if (model.Length is < 1 or > 200 || model.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException("The model name is invalid.", nameof(Model));
        }

        if (ContextTokens is < 2_048 or > 262_144)
        {
            throw new ArgumentOutOfRangeException(nameof(ContextTokens), "Context must be 2,048 to 262,144 tokens.");
        }

        return this with { Endpoint = endpoint, Model = model };
    }
}
