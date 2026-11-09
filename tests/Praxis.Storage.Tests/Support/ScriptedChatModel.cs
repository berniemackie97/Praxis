using Praxis.Inference;

namespace Praxis.Storage.Tests;

/// <summary>A deterministic chat model that answers each request with a scripted response.</summary>
internal sealed class ScriptedChatModel(Func<ChatRequest, string> respond) : IChatModel
{
    public List<ChatRequest> Requests { get; } = [];
    public ModelIdentity Identity { get; init; } = new("ollama", "qwen:9b", "digest-1", "qwen35", "9B", "Q4_K_M");
    public InferenceFailure? DescribeFailure { get; init; }

    public Task<ModelIdentity> DescribeAsync(CancellationToken cancellationToken) =>
        DescribeFailure is { } failure
            ? throw new InferenceException(failure, "unavailable")
            : Task.FromResult(Identity);

    public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new ChatResult(respond(request), 100, 20, TimeSpan.FromSeconds(2)));
    }

    /// <summary>The excerpt text inside the user message.</summary>
    public static string Excerpt(ChatRequest request)
    {
        var user = request.Messages[^1].Content;
        var start = user.IndexOf("<excerpt>\n", StringComparison.Ordinal) + "<excerpt>\n".Length;
        return user[start..user.LastIndexOf("\n</excerpt>", StringComparison.Ordinal)];
    }
}
