namespace Praxis.Application.Inference;

/// <summary>Non-sensitive classification of an inference failure, safe to persist as a code.</summary>
public enum InferenceFailure
{
    /// <summary>The runtime could not be reached.</summary>
    Unavailable,
    /// <summary>The request exceeded its deadline.</summary>
    Timeout,
    /// <summary>The requested model is not installed.</summary>
    ModelMissing,
    /// <summary>The runtime refused the request (client error).</summary>
    Rejected,
    /// <summary>The runtime failed, for example by running out of memory loading a model.</summary>
    ServerError,
    /// <summary>The output stopped at the token limit, so structured output is incomplete.</summary>
    Truncated,
    /// <summary>A subscription plan's usage limit is exhausted until its reset time.</summary>
    QuotaExhausted,
    /// <summary>The provider's sign-in is missing or expired; the owner must sign in again.</summary>
    Unauthenticated,
    /// <summary>The response was malformed or exceeded its size bound.</summary>
    InvalidResponse,
    /// <summary>A noninteractive provider could not request an approval.</summary>
    ApprovalRequired,
    /// <summary>The selected model cannot generate chat replies.</summary>
    ChatNotSupported
}
