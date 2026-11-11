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
    InvalidResponse
}

/// <summary>
/// Raised for expected inference failures. The message never contains prompt or source content;
/// provider error text is not propagated.
/// </summary>
public sealed class InferenceException(InferenceFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public InferenceFailure Failure { get; } = failure;

    /// <summary>Lowercase snake-case code suitable for durable job failure records.</summary>
    public string Code => Failure switch
    {
        InferenceFailure.Unavailable => "model_unavailable",
        InferenceFailure.Timeout => "model_timeout",
        InferenceFailure.ModelMissing => "model_missing",
        InferenceFailure.Rejected => "model_rejected",
        InferenceFailure.ServerError => "model_server_error",
        InferenceFailure.Truncated => "model_truncated",
        InferenceFailure.QuotaExhausted => "model_quota_exhausted",
        InferenceFailure.Unauthenticated => "model_unauthenticated",
        _ => "model_invalid_response"
    };
}
