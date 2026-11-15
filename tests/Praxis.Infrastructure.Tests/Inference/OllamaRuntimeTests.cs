using System.Net;
using System.Text;
using System.Text.Json;
using Praxis.Application.Inference;
using Praxis.Domain.Models;
using Praxis.Infrastructure.Inference.Ollama;
using Xunit;

namespace Praxis.Infrastructure.Tests.Inference;

public sealed class OllamaRuntimeTests
{
    private const string Endpoint = "http://127.0.0.1:11434";
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{"type":"object","properties":{"topics":{"type":"array","items":{"type":"string"}}},"required":["topics"]}""")
        .RootElement;

    private static readonly string Tags = """
        {"models":[{"name":"qwen:9b","digest":"abc123","size":5600000000,
          "details":{"family":"qwen35","parameter_size":"9.0B","quantization_level":"Q4_K_M"}},
         {"name":"other:latest","digest":"def456","size":10}]}
        """;

    [Fact]
    public async Task CompletionSendsBoundedStructuredRequestAndReadsUsage()
    {
        RecordingHandler handler = new RecordingHandler(_ => Json("""
                                                                  {"model":"qwen:9b","message":{"role":"assistant","content":"{\"topics\":[\"SQLite\"]}"},
                                                                   "done":true,"done_reason":"stop","prompt_eval_count":37,"eval_count":12,"total_duration":1500000000}
                                                                  """));
        using OllamaRuntime runtime = new OllamaRuntime(Endpoint, null, handler);

        ChatResult result = await runtime.CreateChatModel("qwen:9b").CompleteAsync(Request(), CancellationToken.None);

        Assert.Equal("""{"topics":["SQLite"]}""", result.Content);
        Assert.Equal(37, result.InputTokens);
        Assert.Equal(12, result.OutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(1.5), result.Duration);

        SentRequest sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://127.0.0.1:11434/api/chat", sent.Uri);
        using JsonDocument body = JsonDocument.Parse(sent.Body!);
        JsonElement root = body.RootElement;
        Assert.Equal("qwen:9b", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("think").GetBoolean());
        Assert.Equal(Schema.GetRawText(), root.GetProperty("format").GetRawText());
        JsonElement options = root.GetProperty("options");
        // Every local call shares one context size so Ollama never restarts the model between them.
        Assert.Equal(8192, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(256, options.GetProperty("num_predict").GetInt32());
        Assert.Equal(0, options.GetProperty("temperature").GetDouble());
        Assert.True(options.TryGetProperty("seed", out _));
        Assert.Equal(["system", "user"], root.GetProperty("messages").EnumerateArray()
            .Select(message => message.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task UntaggedModelNamesResolveToLatest()
    {
        RecordingHandler handler = new RecordingHandler(_ => Json(Tags));
        using OllamaRuntime runtime = new OllamaRuntime(Endpoint, null, handler);

        ModelIdentity identity = await runtime.CreateChatModel("other").DescribeAsync(CancellationToken.None);

        Assert.Equal(new ModelIdentity("ollama", "other:latest", "def456", null, null, null), identity);
    }

    [Fact]
    public async Task DescribeReportsExactBuildAndMissingModels()
    {
        RecordingHandler handler = new RecordingHandler(_ => Json(Tags));
        using OllamaRuntime runtime = new OllamaRuntime(Endpoint, null, handler);

        ModelIdentity identity = await runtime.CreateChatModel("qwen:9b").DescribeAsync(CancellationToken.None);
        Assert.Equal(new ModelIdentity("ollama", "qwen:9b", "abc123", "qwen35", "9.0B", "Q4_K_M"), identity);

        InferenceException missing = await Assert.ThrowsAsync<InferenceException>(() =>
            runtime.CreateChatModel("absent:1b").DescribeAsync(CancellationToken.None));
        Assert.Equal(InferenceFailure.ModelMissing, missing.Failure);

        IReadOnlyList<InstalledModel> models = await runtime.ListModelsAsync(CancellationToken.None);
        Assert.Equal(["qwen:9b", "other:latest"], models.Select(model => model.Name));
        Assert.Equal(5_600_000_000, models[0].SizeBytes);
    }

    [Fact]
    public async Task DiscoverCapabilitiesByExactTagWithoutReadingOrSendingChatContent()
    {
        RecordingHandler handler = new RecordingHandler(_ => Json("""{"capabilities":["tools","embedding"]}"""));
        using OllamaRuntime runtime = new(Endpoint, null, handler);

        LocalModelCapabilities? capabilities = await runtime.ReadCapabilitiesAsync("renamed:latest", CancellationToken.None);

        Assert.Equal(new LocalModelCapabilities(false, true, true), capabilities);
        Assert.False(new InstalledModel("renamed:latest", "digest", 1, capabilities).SupportsChat);
        SentRequest sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://127.0.0.1:11434/api/show", sent.Uri);
        Assert.Equal("""{"model":"renamed:latest"}""", sent.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, InferenceFailure.ModelMissing, "model_missing")]
    [InlineData(HttpStatusCode.BadRequest, InferenceFailure.Rejected, "model_rejected")]
    [InlineData(HttpStatusCode.InternalServerError, InferenceFailure.ServerError, "model_server_error")]
    public async Task HttpFailuresMapToCodesWithoutProviderText(
        HttpStatusCode status, InferenceFailure failure, string code)
    {
        RecordingHandler handler = new RecordingHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"error":"secret provider detail /Users/me/private.txt"}""")
        });
        using OllamaRuntime runtime = new OllamaRuntime(Endpoint, null, handler);

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(() =>
            runtime.CreateChatModel("qwen:9b").CompleteAsync(Request(), CancellationToken.None));

        Assert.Equal(failure, exception.Failure);
        Assert.Equal(code, exception.Code);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableRuntimeIsUnavailable()
    {
        RecordingHandler handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        using OllamaRuntime runtime = new OllamaRuntime(Endpoint, null, handler);

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(() =>
            runtime.ListModelsAsync(CancellationToken.None));
        Assert.Equal(InferenceFailure.Unavailable, exception.Failure);
    }

    [Fact]
    public async Task TruncatedMalformedAndOversizedResponsesAreRejected()
    {
        async Task<InferenceFailure> FailureFor(string body)
        {
            using OllamaRuntime runtime = new OllamaRuntime(Endpoint, null, new RecordingHandler(_ => Json(body)));
            InferenceException exception = await Assert.ThrowsAsync<InferenceException>(() =>
                runtime.CreateChatModel("qwen:9b").CompleteAsync(Request(), CancellationToken.None));
            return exception.Failure;
        }

        Assert.Equal(InferenceFailure.Truncated, await FailureFor(
            """{"message":{"content":"{\"topi"},"done":true,"done_reason":"length"}"""));
        Assert.Equal(InferenceFailure.InvalidResponse, await FailureFor("{not json"));
        Assert.Equal(InferenceFailure.InvalidResponse, await FailureFor("""{"done":false}"""));
        Assert.Equal(InferenceFailure.InvalidResponse, await FailureFor(
            "\"" + new string('x', OllamaRuntime.MaximumResponseBytes) + "\""));
    }

    [Fact]
    public async Task DeadlineIsTimeoutButCallerCancellationIsCancellation()
    {
        RecordingHandler handler = new RecordingHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        });
        using OllamaRuntime runtime = new OllamaRuntime(Endpoint, TimeSpan.FromSeconds(1), handler);

        InferenceException timeout = await Assert.ThrowsAsync<InferenceException>(() =>
            runtime.ListModelsAsync(CancellationToken.None));
        Assert.Equal(InferenceFailure.Timeout, timeout.Failure);

        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.ListModelsAsync(cancellation.Token));
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://localhost:11434/")]
    [InlineData("https://[::1]:8443")]
    public void LoopbackOriginsAreAccepted(string endpoint) =>
        Assert.True(LoopbackEndpoint.Validate(endpoint).IsLoopback);

    [Theory]
    [InlineData("http://192.168.1.20:11434")]
    [InlineData("http://ollama.example.com:11434")]
    [InlineData("http://127.0.0.1:11434/api")]
    [InlineData("http://user:pass@127.0.0.1:11434")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("127.0.0.1:11434")]
    public void NonLoopbackOrNonOriginEndpointsAreRejected(string endpoint) =>
        Assert.Throws<ArgumentException>(() => LoopbackEndpoint.Validate(endpoint));

    [Fact]
    public async Task InvalidRequestsAreRejectedBeforeSending()
    {
        RecordingHandler handler = new RecordingHandler(_ => Json("{}"));
        using OllamaRuntime runtime = new OllamaRuntime(Endpoint, null, handler);
        IChatModel model = runtime.CreateChatModel("qwen:9b");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            model.CompleteAsync(Request() with { MaxOutputTokens = 4096 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => model.CompleteAsync(
            Request() with { ResponseSchema = JsonDocument.Parse("[]").RootElement }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            model.CompleteAsync(Request() with { Messages = [] }, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => runtime.CreateChatModel("bad name"));
        Assert.Empty(handler.Requests);
    }

    private static ChatRequest Request() => new(
        [new ChatMessage(ChatRole.System, "Extract topics."), new ChatMessage(ChatRole.User, "SQLite FTS5.")],
        Schema, MaxOutputTokens: 256, ContextTokens: 4096);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed record SentRequest(HttpMethod Method, string Uri, string? Body);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new SentRequest(request.Method, request.RequestUri!.ToString(), body));
            return await respond(request, cancellationToken);
        }
    }
}
