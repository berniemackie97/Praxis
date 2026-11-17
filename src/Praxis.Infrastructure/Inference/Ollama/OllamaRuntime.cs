using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Praxis.Application.Inference;
using Praxis.Domain.Models;

namespace Praxis.Infrastructure.Inference.Ollama;

/// <summary>
/// A local Ollama runtime reached over a loopback origin. Lists installed models and creates
/// chat models. Every request is bounded in time and response size, and failures surface as
/// <see cref="InferenceException"/> codes without provider error text.
/// </summary>
public sealed class OllamaRuntime : IDisposable
{
    public const string ProviderName = ModelProfile.OllamaProvider;
    internal const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(3);
    private readonly HttpClient http;
    private readonly TimeSpan requestTimeout;

    public OllamaRuntime(string endpoint, TimeSpan? requestTimeout = null)
        : this(endpoint, requestTimeout, new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
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

    internal async Task<JsonElement> RequireLocalModelAsync(string model, CancellationToken cancellationToken)
    {
        string tag = model.Contains(':', StringComparison.Ordinal) ? model : model + ":latest";
        using JsonDocument document = await SendAsync(HttpMethod.Get, "api/tags", null, cancellationToken);
        if (!document.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
        {
            throw Invalid();
        }

        foreach (JsonElement installed in models.EnumerateArray())
        {
            if (RequiredString(installed, "name") != tag)
            {
                continue;
            }

            if (tag.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) ||
                tag.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase) ||
                HasText(installed, "remote_model") || HasText(installed, "remote_host"))
            {
                throw new InferenceException(InferenceFailure.Rejected, "Private Ollama requests require a locally installed model, not a cloud model.");
            }

            return installed.Clone();
        }

        throw new InferenceException(InferenceFailure.ModelMissing, "The requested model is not installed in the local runtime.");
    }

    private static bool HasText(JsonElement model, string key) =>
        model.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());

    internal async Task<LocalModelCapabilities?> ReadCapabilitiesAsync(string model, CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendAsync(HttpMethod.Post, "api/show", JsonSerializer.SerializeToUtf8Bytes(new { model }), cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("capabilities", out JsonElement values) ||
            values.ValueKind != JsonValueKind.Array || values.GetArrayLength() == 0)
        {
            return null;
        }

        var capabilities = values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        return new LocalModelCapabilities(capabilities.Contains("completion"), capabilities.Contains("tools"), capabilities.Contains("embedding"), capabilities.Contains("vision"));
    }

    public IChatModel CreateChatModel(string model) => new OllamaChatModel(this, model);

    public IEmbeddingModel CreateEmbeddingModel(string model) => new OllamaEmbeddingModel(this, model);

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
