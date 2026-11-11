using System.Net;
using System.Text;
using System.Text.Json;
using Praxis.Application.Inference;
using Praxis.Infrastructure.Inference.Ollama;
using Xunit;

namespace Praxis.Infrastructure.Tests.Inference;

public sealed class OllamaEmbeddingTests
{
    private const string Endpoint = "http://127.0.0.1:11434";

    [Fact]
    public async Task BoundedLocalEmbeddingsUseNonTruncatingEndpointAndValidateVectorOrder()
    {
        RecordingHandler handler = new(_ => Reply("""
                                                    {"model":"embeddinggemma:latest",
                                                     "embeddings":[[3,4],[0,2]],
                                                     "total_duration":2000000}
                                                    """));
        using OllamaRuntime runtime = new(Endpoint, null, handler);
        IEmbeddingModel embeddings = runtime.CreateEmbeddingModel("embeddinggemma");

        IReadOnlyList<float[]> output = await embeddings.EmbedAsync(["first", "second"], CancellationToken.None);
        Assert.Equal(2, output.Count);
        Assert.Equal(0.6f, output[0][0], 4);
        Assert.Equal(0.8f, output[0][1], 4);
        Assert.Equal(0f, output[1][0], 4);
        Assert.Equal(1f, output[1][1], 4);

        Sent sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("http://127.0.0.1:11434/api/embed", sent.Url);
        using JsonDocument request = JsonDocument.Parse(sent.Body);
        Assert.Equal("embeddinggemma:latest", request.RootElement.GetProperty("model").GetString());
        Assert.False(request.RootElement.GetProperty("truncate").GetBoolean());
        Assert.Equal(["first", "second"],
            request.RootElement.GetProperty("input").EnumerateArray()
                .Select(input => input.GetString()));
    }

    [Theory]
    [InlineData("""{"embeddings":[]}""")]
    [InlineData("""{"embeddings":[[]]}""")]
    [InlineData("""{"embeddings":[[0,0]]}""")]
    [InlineData("""{"embeddings":[[1,"bad"]]}""")]
    [InlineData("""{"embeddings":[[1,2],[3,4]]}""")]
    public async Task InvalidOrMismatchedResponseIsRejected(string body)
    {
        using OllamaRuntime runtime = new(Endpoint, null, new RecordingHandler(_ => Reply(body)));
        InferenceException error = await Assert.ThrowsAsync<InferenceException>(() =>
            runtime.CreateEmbeddingModel("fixture").EmbedAsync(["text"], CancellationToken.None));
        Assert.Equal(InferenceFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public async Task InputAndModelValidationRunBeforeAnyNetworkRequest()
    {
        RecordingHandler handler = new(_ => Reply("{}"));
        using OllamaRuntime runtime = new(Endpoint, null, handler);
        Assert.Throws<ArgumentException>(() => runtime.CreateEmbeddingModel("model with spaces"));
        IEmbeddingModel model = runtime.CreateEmbeddingModel("embeddinggemma");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            model.EmbedAsync([], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            model.EmbedAsync([new string('a', 2_001)], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            model.EmbedAsync(Enumerable.Repeat("text", 9).ToArray(), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed record Sent(HttpMethod Method, string Url, string Body);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? "" :
                await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Sent(request.Method, request.RequestUri!.ToString(), body));
            return respond(request);
        }
    }
}
