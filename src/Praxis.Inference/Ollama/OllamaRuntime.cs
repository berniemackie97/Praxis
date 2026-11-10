using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Praxis.Inference.Ollama;

public sealed record InstalledModel(string Name, string Digest, long SizeBytes);

/// <summary>
/// A local Ollama runtime reached over a loopback origin. Lists installed models and creates
/// chat models. Every request is bounded in time and response size, and failures surface as
/// <see cref="InferenceException"/> codes without provider error text.
/// </summary>
public sealed class OllamaRuntime : IDisposable
{
    public const string ProviderName = "ollama";
    internal const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(3);
    private readonly HttpClient http;
    private readonly TimeSpan requestTimeout;

    public OllamaRuntime(string endpoint, TimeSpan? requestTimeout = null)
        : this(endpoint, requestTimeout, new SocketsHttpHandler { UseProxy = false })
    {
    }

    internal OllamaRuntime(string endpoint, TimeSpan? requestTimeout, HttpMessageHandler handler)
    {
        this.requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (this.requestTimeout < TimeSpan.FromSeconds(1) || this.requestTimeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        http = new HttpClient(handler)
        {
            BaseAddress = LoopbackEndpoint.Validate(endpoint),
            Timeout = Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IReadOnlyList<InstalledModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendAsync(HttpMethod.Get, "api/tags", null, cancellationToken);
        if (!document.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
        {
            throw Invalid();
        }

        return models.EnumerateArray().Select(model => new InstalledModel(RequiredString(model, "name"), RequiredString(model, "digest"), model.TryGetProperty("size", out JsonElement size) && size.TryGetInt64(out long bytes) ? bytes : 0)).ToList();
    }

    public IChatModel CreateChatModel(string model) => new OllamaChatModel(this, model);

    internal async Task<JsonDocument> SendAsync(HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);
        using HttpRequestMessage request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw StatusFailure(response.StatusCode);
            }

            byte[] payload = await ReadBoundedAsync(response.Content, deadline.Token);
            try
            {
                return JsonDocument.Parse(payload);
            }
            catch (JsonException exception)
            {
                throw new InferenceException(InferenceFailure.InvalidResponse, "The local model runtime returned malformed JSON.", exception);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InferenceException(InferenceFailure.Timeout, "The local model request exceeded its time budget.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new InferenceException(InferenceFailure.Unavailable, "The local model runtime is not reachable.", exception);
        }
    }

    internal static string RequiredString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text ? text : throw Invalid();

    internal static InferenceException Invalid() => new(InferenceFailure.InvalidResponse, "The local model runtime returned an unexpected response.");

    private static InferenceException StatusFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => new InferenceException(InferenceFailure.ModelMissing, "The requested model is not installed in the local runtime."),
        >= HttpStatusCode.InternalServerError => new InferenceException(InferenceFailure.ServerError, "The local model runtime failed to serve the request."),
        _ => new InferenceException(InferenceFailure.Rejected, "The local model runtime rejected the request.")
    };

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InferenceException(InferenceFailure.InvalidResponse, "The local model response exceeds its size bound.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new InferenceException(InferenceFailure.InvalidResponse, "The local model response exceeds its size bound.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public void Dispose() => http.Dispose();
}
