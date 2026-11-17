using Praxis.Application.Inference;
using Praxis.Application.Models;
using Praxis.Application.Policy;
using Praxis.Domain.Models;
using Praxis.Domain.Search;

namespace Praxis.Application.Search;

/// <summary>Incremental local indexing; unchanged, empty indexes do not repeatedly wake inference.</summary>
public sealed class SemanticIndexMaintenance(
    ISemanticVectorStore vectors, IModelProfileRepository profiles, IEmbeddingModelConnector embeddings, TimeProvider clock, ModelActivity? activity = null)
{
    private ModelProfile? cachedProfile;
    private string? cachedDigest;
    private DateTimeOffset checkedAt;

    public async Task<int> RunNextAsync(CancellationToken cancellationToken)
    {
        if (profiles.Get(ModelProfile.LocalEmbedding) is not { Enabled: true } profile)
        {
            return 0;
        }

        if (profile == cachedProfile && cachedDigest is not null && clock.GetUtcNow() - checkedAt < TimeSpan.FromMinutes(10) &&
            vectors.ReadSegmentsToIndex(cachedDigest, false, 1).Count == 0)
        {
            return 0;
        }

        using IDisposable? running = activity?.Begin(new ActiveModelCall("embedding", "ollama", profile.Model, "indexing", null, clock.GetUtcNow()));
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using EmbeddingModelConnection connection = embeddings.Connect(profile, TimeSpan.FromSeconds(20));
        ModelIdentity identity = await connection.Model.DescribeAsync(deadline.Token);
        cachedDigest = EmbeddingVectors.ValidateModelDigest(identity.Digest);
        cachedProfile = profile;
        checkedAt = clock.GetUtcNow();
        SemanticIndexUpdate update = await new SemanticIndexer(vectors).RefreshAsync(connection.Model, maxSegments: 32,
            cancellationToken: deadline.Token, mayCommit: () => profiles.Get(ModelProfile.LocalEmbedding) == profile);
        return update.Indexed;
    }
}
