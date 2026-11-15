namespace Praxis.Application.Inference;

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
        InferenceFailure.ApprovalRequired => "model_approval_required",
        InferenceFailure.ChatNotSupported => "model_chat_unsupported",
        _ => "model_invalid_response"
    };
}
