using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Inference;
using Praxis.Application.Models;
using Praxis.Application.Processing;
using Praxis.Domain.Models;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Owner choice of local enrichment and embedding models.</summary>
public sealed class ModelActions(
    IModelProfileRepository profiles, ILocalModelDirectory models, ProcessingPipeline? processing = null) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("modelProfile", _ => new ModelProfileResponse(profiles.Get(ModelProfile.LocalEnrichment))),
        DesktopAction.Of("modelProfileSave", fields => Save(fields, ModelProfile.LocalEnrichment)),
        DesktopAction.Of("embeddingProfile", _ => new ModelProfileResponse(profiles.Get(ModelProfile.LocalEmbedding))),
        DesktopAction.Of("embeddingProfileSave", fields => Save(fields, ModelProfile.LocalEmbedding)),
        DesktopAction.OfAsync("localModels", async (fields, cancellationToken) =>
            new LocalModelsResponse(await models.ListInstalledAsync(fields.RequireString("endpoint", 256), cancellationToken)))
    ];

    private ModelProfileResponse Save(DesktopRequestFields fields, string role)
    {
        ModelProfile saved = profiles.Save(new ModelProfile(
            role,
            fields.RequireString("provider", 32),
            fields.RequireString("endpoint", 256),
            fields.RequireString("model", 200),
            fields.IntegerOrDefault("contextTokens", 16_384, 2_048, 262_144),
            fields.RequireBoolean("enabled")));
        // A model change applies to the next job; waking clears any runtime backoff.
        processing?.Wake();
        return new ModelProfileResponse(saved);
    }
}
