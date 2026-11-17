using System.Text.Json;

namespace Praxis.Application.Inference;

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

        foreach (ChatMessage message in Messages)
        {
            if (message.Images is { } images && (images.Count > 4 || images.Any(image => image.Length is < 1 or > 160_000 ||
                !Convert.TryFromBase64String(image, new byte[120_000], out _))))
            {
                throw new ArgumentException("A visual request contains too many or invalid images.");
            }
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
