using System.Text;

namespace Praxis.Application.Verification;

/// <summary>Conservative UTF-8 bound for an already-reviewed verification request, including schema and output headroom.</summary>
internal sealed record VerificationBudget(int ContextTokens, int OutputTokens)
{
    internal static VerificationBudget? For(string payload, int itemCount)
    {
        int output = Math.Clamp(itemCount * 352, 1_024, 4_096);
        int required = VerificationPrompt.Messages(payload).Sum(message => Encoding.UTF8.GetByteCount(message.Content)) +
            Encoding.UTF8.GetByteCount(VerificationPrompt.Schema.GetRawText()) + output + 1_024;
        return required switch
        {
            <= 8_192 => new(8_192, output),
            <= 16_384 => new(16_384, output),
            <= 32_768 => new(32_768, output),
            _ => null
        };
    }
}
