using System.Text.Json;

namespace Praxis.Inference.Ollama;

/// <summary>Bounded text embeddings through the local Ollama /api/embed endpoint.</summary>
internal sealed class OllamaEmbeddingModel : IEmbeddingModel
{
    private readonly OllamaRuntime runtime;
    private readonly string model;

    internal OllamaEmbeddingModel(OllamaRuntime runtime, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (model.Length > 200 || model.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException("The embedding model name is invalid.", nameof(model));
        }

        this.runtime = runtime;
        this.model = model.Contains(':', StringComparison.Ordinal) ? model : model + ":latest";
    }

    public Task<ModelIdentity> DescribeAsync(CancellationToken cancellationToken) =>
        runtime.CreateChatModel(model).DescribeAsync(cancellationToken);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count is < 1 or > 8 || inputs.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 2_000))
        {
            throw new ArgumentException("Embed one to eight nonempty text segments of at most 2,000 characters.", nameof(inputs));
        }

        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new { model, input = inputs, truncate = false });
        using JsonDocument response = await runtime.SendAsync(HttpMethod.Post, "api/embed", request, cancellationToken);
        JsonElement root = response.RootElement;
        if (!root.TryGetProperty("embeddings", out JsonElement embeddings) ||
            embeddings.ValueKind != JsonValueKind.Array || embeddings.GetArrayLength() != inputs.Count)
        {
            throw OllamaRuntime.Invalid();
        }

        List<float[]> result = new(inputs.Count);
        int dimension = 0;
        foreach (JsonElement vector in embeddings.EnumerateArray())
        {
            if (vector.ValueKind != JsonValueKind.Array || vector.GetArrayLength() is < 1 or > 4_096 ||
                (dimension != 0 && vector.GetArrayLength() != dimension))
            {
                throw OllamaRuntime.Invalid();
            }

            dimension = vector.GetArrayLength();
            float[] values = new float[dimension];
            int index = 0;
            double magnitude = 0;
            foreach (JsonElement entry in vector.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Number || !entry.TryGetSingle(out float value) ||
                    !float.IsFinite(value))
                {
                    throw OllamaRuntime.Invalid();
                }

                values[index++] = value;
                magnitude += (double)value * value;
            }

            if (!double.IsFinite(magnitude) || magnitude <= 0)
            {
                throw OllamaRuntime.Invalid();
            }

            float inverse = (float)(1d / Math.Sqrt(magnitude));
            for (int i = 0; i < values.Length; i++)
            {
                values[i] *= inverse;
            }

            result.Add(values);
        }

        return result;
    }
}
