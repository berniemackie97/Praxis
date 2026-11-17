using System.Text.Json;

using Praxis.Application.Inference;
using Praxis.Domain.Models;

namespace Praxis.Infrastructure.Inference.Ollama;

/// <summary>
/// Structured chat completion through Ollama's native API: non-streaming, reasoning output
/// disabled, JSON-schema constrained, fixed seed, bounded context and output.
/// </summary>
internal sealed class OllamaChatModel : IChatModel
{
    private const int Seed = 7;
    private readonly OllamaRuntime runtime;
    private readonly string model;

    internal OllamaChatModel(OllamaRuntime runtime, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.Length > 200 || model.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException("The model name is invalid.", nameof(model));
        }

        this.runtime = runtime;
        this.model = model.Contains(':', StringComparison.Ordinal) ? model : model + ":latest";
    }

    public async Task<ModelIdentity> DescribeAsync(CancellationToken cancellationToken)
    {
        JsonElement installed = await runtime.RequireLocalModelAsync(model, cancellationToken);
        installed.TryGetProperty("details", out JsonElement details);
        return new ModelIdentity(ModelProfile.OllamaProvider, model, OllamaRuntime.RequiredString(installed, "digest"),
            OptionalString(details, "family"), OptionalString(details, "parameter_size"), OptionalString(details, "quantization_level"));
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        using JsonDocument document = await runtime.SendAsync(HttpMethod.Post, "api/chat", Serialize(request), cancellationToken);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("done", out JsonElement done) || done.ValueKind != JsonValueKind.True || !root.TryGetProperty("message", out JsonElement message))
        {
            throw OllamaRuntime.Invalid();
        }

        if (OptionalString(root, "done_reason") == "length")
        {
            throw new InferenceException(InferenceFailure.Truncated, "The model output reached its token limit before completing.");
        }

        return new ChatResult(OllamaRuntime.RequiredString(message, "content"), OptionalInt32(root, "prompt_eval_count"),
            OptionalInt32(root, "eval_count"), TimeSpan.FromTicks(OptionalInt64(root, "total_duration") / 100));
    }

    private byte[] Serialize(ChatRequest request)
    {
        using MemoryStream buffer = new MemoryStream();
        using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteBoolean("stream", false);
            writer.WriteBoolean("think", false);
            // Keep the one local model loaded between intake calls; reloading it costs minutes on
            // smaller Macs, especially when models live on an external disk.
            writer.WriteString("keep_alive", KeepAlive);
            writer.WritePropertyName("format");
            request.ResponseSchema.WriteTo(writer);
            writer.WriteStartArray("messages");
            foreach (ChatMessage entry in request.Messages)
            {
                writer.WriteStartObject();
                writer.WriteString("role", entry.Role switch
                {
                    ChatRole.System => "system",
                    ChatRole.Assistant => "assistant",
                    _ => "user"
                });
                writer.WriteString("content", entry.Content);
                if (entry.Images is { Count: > 0 } images)
                {
                    writer.WriteStartArray("images");
                    foreach (string image in images)
                    {
                        writer.WriteStringValue(image);
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject("options");
            writer.WriteNumber("num_ctx", ResidentContext(request.ContextTokens));
            writer.WriteNumber("num_predict", request.MaxOutputTokens);
            writer.WriteNumber("temperature", request.Temperature);
            writer.WriteNumber("seed", Seed);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string? OptionalString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int OptionalInt32(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number) && number >= 0 ? number : 0;

    private static long OptionalInt64(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetInt64(out long number) && number >= 0 ? number : 0;

    /// <summary>
    /// The context size sent to Ollama, in a few fixed steps. Ollama restarts the model whenever the
    /// context size changes, so triage, drafts, enrichment and chat share 8,192 tokens and only a
    /// request that needs more moves to a larger step.
    /// </summary>
    internal static int ResidentContext(int requested) => requested switch
    {
        <= 8_192 => 8_192,
        <= 16_384 => 16_384,
        _ => 32_768
    };

    internal const string KeepAlive = "30m";

}
