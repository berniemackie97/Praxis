namespace Praxis.Application.Inference;

/// <summary>A locally configured model that embeds bounded text for retrieval.</summary>
public interface IEmbeddingModel
{
    Task<ModelIdentity> DescribeAsync(CancellationToken cancellationToken);

    /// <summary>One vector per input, in the same order. Outputs are not assumed trusted.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken);
}
