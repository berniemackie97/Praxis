namespace Praxis.Application.Processing;

/// <summary>
/// Raised by a handler whose required runtime is not available (for example, the local model
/// is disabled or not running). The pipeline returns the job to the queue without consuming
/// an attempt and backs off that handler; the job is not failed.
/// </summary>
public sealed class ProcessorUnavailableException(string code, Exception? inner = null)
    : Exception("The processor's runtime is unavailable: " + code, inner)
{
    public string Code { get; } = code;
}
