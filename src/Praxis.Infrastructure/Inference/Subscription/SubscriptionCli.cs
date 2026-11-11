using System.Text;

using Praxis.Application.Inference;

namespace Praxis.Application.Inference;

/// <summary>Shared rules for provider CLIs that run on the owner's subscription sign-in.</summary>
internal static class SubscriptionCli
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private static readonly string[] AuthenticationMarkers =
        ["failed to authenticate", "not logged in", "please log in", "please login", "oauth", "sign in again",
         "unauthorized", "401"];

    private static readonly string[] QuotaMarkers =
        ["usage limit", "rate limit", "rate_limit", "limit reached", "hit your", "quota", "too many requests",
         "429", "temporarily limiting"];

    internal static string ValidateExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) ||
            !File.Exists(executable))
        {
            throw new ArgumentException("The provider command must be an existing absolute path.", nameof(executable));
        }

        return executable;
    }

    internal static string ValidateModel(string? model)
    {
        if (model is null || model.Length is < 1 or > 100 ||
            model.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '.' or '_' or ':')))
        {
            throw new ArgumentException("The provider model name is invalid.", nameof(model));
        }

        return model;
    }

    internal static string System(ChatRequest request) => Join(request, ChatRole.System);

    internal static string User(ChatRequest request) => Join(request, ChatRole.User);

    /// <summary>
    /// Maps a provider's failure text to a code. Only the classification leaves this method;
    /// the provider text is never stored or surfaced.
    /// </summary>
    internal static InferenceException Classify(string providerText, string fallback)
    {
        var text = providerText.ToLowerInvariant();
        if (AuthenticationMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
        {
            return new InferenceException(InferenceFailure.Unauthenticated,
                "The provider sign-in is missing or expired.");
        }

        if (QuotaMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)))
        {
            return new InferenceException(InferenceFailure.QuotaExhausted,
                "The provider plan's usage limit is exhausted.");
        }

        return new InferenceException(InferenceFailure.InvalidResponse, fallback);
    }

    private static string Join(ChatRequest request, ChatRole role)
    {
        var builder = new StringBuilder();
        foreach (var message in request.Messages.Where(message => message.Role == role))
        {
            builder.Append(message.Content).Append("\n\n");
        }

        return builder.ToString().TrimEnd();
    }
}
