using System.Net;
using Praxis.Application.Inference;

namespace Praxis.Infrastructure.Agents;

/// <summary>Maps provider HTTP failures to bounded inference failures without propagating provider text.</summary>
internal static class HttpAgentErrors
{
    internal static InferenceException From(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => new InferenceException(InferenceFailure.Unauthenticated, "The provider rejected the credentials."),
        404 => new InferenceException(InferenceFailure.ModelMissing, "The provider does not offer that model."),
        408 => new InferenceException(InferenceFailure.Timeout, "The provider timed out."),
        429 => new InferenceException(InferenceFailure.QuotaExhausted, "The provider rate limit or quota was reached."),
        >= 500 => new InferenceException(InferenceFailure.ServerError, "The provider failed."),
        _ => new InferenceException(InferenceFailure.Rejected, "The provider rejected the request.")
    };

    internal static async Task<T> SendAsync<T>(Func<Task<T>> send, CancellationToken cancellationToken)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null)
        {
            throw new InferenceException(InferenceFailure.Unavailable, "The provider could not be reached.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InferenceException(InferenceFailure.Timeout, "The provider timed out.", exception);
        }
    }
}
