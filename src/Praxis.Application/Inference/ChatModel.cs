using System.Text.Json;

namespace Praxis.Application.Inference;

public enum ChatRole
{
    System,
    User,
    Assistant
}

public sealed record ChatMessage(ChatRole Role, string Content);

/// <summary>
/// One bounded, structured completion. The response must conform to
/// <see cref="ResponseSchema"/> (a JSON Schema object); free-form output is not requested.
/// </summary>
public sealed record ChatRequest(IReadOnlyList<ChatMessage> Messages, JsonElement ResponseSchema, int MaxOutputTokens, int ContextTokens, double Temperature = 0)
{
    public const int MaximumMessages = 32;
    public const int MaximumMessageCharacters = 64 * 1024;

    public void Validate()
    {
        if (Messages is null || Messages.Count is < 1 or > MaximumMessages || Messages.Any(message => string.IsNullOrEmpty(message.Content) || message.Content.Length > MaximumMessageCharacters))
        {
            throw new ArgumentException("Provide one to 32 nonempty, bounded messages.", nameof(Messages));
        }

        if (ResponseSchema.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("The response schema must be a JSON object.", nameof(ResponseSchema));
        }

        if (MaxOutputTokens is < 1 or > 8_192)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOutputTokens));
        }

        if (ContextTokens is < 1_024 or > 262_144 || MaxOutputTokens >= ContextTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(ContextTokens));
        }

        if (!double.IsFinite(Temperature) || Temperature is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(Temperature));
        }
    }
}

public sealed record ChatResult(string Content, int InputTokens, int OutputTokens, TimeSpan Duration);

/// <summary>Identity recorded with every model-produced result for provenance and cache keys.</summary>
public sealed record ModelIdentity(string Provider, string Model, string Digest, string? Family, string? ParameterSize, string? Quantization);

/// <summary>A model that completes structured chat requests.</summary>
public interface IChatModel
{
    /// <summary>Resolves the exact model build that will serve requests.</summary>
    Task<ModelIdentity> DescribeAsync(CancellationToken cancellationToken);

    Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);
}
