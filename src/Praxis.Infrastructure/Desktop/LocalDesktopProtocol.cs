using System.Text.Json;
using Praxis.Application.Inference;
using Praxis.Infrastructure.Catalog;
using Praxis.Infrastructure.Inference.Ollama;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Models;
using Praxis.Infrastructure.Processing;
using Praxis.Infrastructure.Search;
using Praxis.Infrastructure.Verification;

namespace Praxis.Infrastructure.Desktop;

public sealed class LocalDesktopProtocol(LocalCatalog catalog, LocalQuickCapture? quickCapture = null, LocalWatchedIntake? monitor = null, LocalProcessingPipeline? processing = null, LocalVerificationRunner? verification = null, LocalWatchedIntakeGroup? group = null)
{
    public const int MaximumRequestCharacters = 16_384;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool StopRequested { get; private set; }

    public async Task<string> HandleAsync(string request, CancellationToken cancellationToken = default)
    {
        string? requestId = null;
        try
        {
            if (request.Length is < 2 or > MaximumRequestCharacters)
            {
                throw new ArgumentException("The desktop request exceeds its size limit.");
            }

            using JsonDocument document = JsonDocument.Parse(request);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("A request object is required.");
            }

            DesktopRequestFields fields = new(root);
            requestId = fields.RequireString("requestId", 64);
            string action = fields.RequireString("action", 32);
            object result = action switch
            {
                "library" => new
                {
                    items = catalog.Library.List(fields.IntegerOrDefault("limit", 100, 1, 200),
                        fields.IntegerOrDefault("offset", 0, 0, 1_000_000),
                        fields.OptionalGuid("collectionId"))
                },
                "search" => await SearchAsync(fields, cancellationToken),
                "semanticIndex" => await RefreshSemanticIndexAsync(fields, cancellationToken),
                "mediaCues" => new
                {
                    clips = catalog.Media.SearchCues(fields.RequireString("query", 256),
                        fields.IntegerOrDefault("limit", 20, 1, 50))
                },
                "mediaInspection" => new
                {
                    media = await catalog.Media.InspectAsync(fields.RequireGuid("sourceId"),
                        fields.OptionalInteger("revision", 1, int.MaxValue), cancellationToken)
                },
                "collections" => new
                {
                    collections = catalog.Collections.List(fields.IntegerOrDefault("limit", 100, 1, 200),
                        fields.IntegerOrDefault("offset", 0, 0, 1_000_000))
                },
                "collectionCreate" => new
                {
                    collection = catalog.Collections.Create(fields.RequireString("name", 80))
                },
                "collectionRename" => new
                {
                    changed = catalog.Collections.Rename(fields.RequireGuid("collectionId"),
                        fields.RequireString("name", 80))
                },
                "collectionArchive" => new
                {
                    changed = catalog.Collections.Archive(fields.RequireGuid("collectionId"))
                },
                "collectionAdd" => new
                {
                    changed = catalog.Collections.AddSource(fields.RequireGuid("collectionId"), fields.RequireGuid("sourceId"))
                },
                "collectionRemove" => new
                {
                    changed = catalog.Collections.RemoveSource(fields.RequireGuid("collectionId"), fields.RequireGuid("sourceId"))
                },
                "sourceCollections" => new
                {
                    collectionIds = catalog.Collections.ListForSource(fields.RequireGuid("sourceId"))
                },
                "sourceMoves" => new
                {
                    moves = catalog.Sources.ListMovements(fields.RequireGuid("sourceId"), fields.IntegerOrDefault("limit", 20, 1, 100))
                },
                "noteCapture" => await CaptureNoteAsync(fields, cancellationToken),
                "topics" => new
                {
                    proposals = catalog.Proposals.List(
                        fields.RequireGuid("sourceId"), fields.IntegerOrDefault("limit", 40, 1, 100)),
                    outcome = catalog.Proposals.Outcome(fields.RequireGuid("sourceId")),
                    semanticOutcome = catalog.Proposals.Outcome(
                        fields.RequireGuid("sourceId"), LocalProcessors.SemanticExtraction)
                },
                "topicReview" => new
                {
                    reviewed = catalog.Proposals.Review(
                        fields.RequireGuid("proposalId"),
                        fields.RequireString("decision", 16),
                        fields.OptionalText("correction", 80, "The review correction is invalid."))
                },
                "exportOriginal" => new
                {
                    exported = await catalog.Originals.ExportAsync(fields.RequireGuid("itemId"), fields.IntegerOrDefault("revision", 1, 1, int.MaxValue),
                        fields.RequireString("destinationPath", 4_096), cancellationToken)
                },
                "intakeStatus" => new
                {
                    status = IntakeStatus(),
                    roots = group?.RootStatuses()
                },
                "intakePause" => new
                {
                    status = PauseIntake(fields.RequireBoolean("paused")),
                    roots = group?.RootStatuses()
                },
                "intakeRescan" => new
                {
                    status = RescanIntake(),
                    roots = group?.RootStatuses()
                },
                "intakeApproveRoot" => new
                {
                    status = RequireGroup().ApproveRoot(fields.RequireString("path", 4_096)),
                    roots = group?.RootStatuses()
                },
                "preview" => Preview(fields),
                "verify" => await VerifyAsync(fields, cancellationToken),
                "retry" => Retry(fields),
                "entities" => new
                {
                    entities = catalog.Knowledge.ListEntities(
                        fields.OptionalText("entityType", 32, "The entity type is invalid."),
                        fields.OptionalText("search", 120, "The entity search is invalid."),
                        fields.IntegerOrDefault("limit", 100, 1, 500))
                },
                "entity" => new { entity = catalog.Knowledge.GetEntity(fields.RequireGuid("entityId")) },
                "entityRename" => new
                {
                    changed = catalog.Knowledge.Rename(fields.RequireGuid("entityId"), fields.RequireString("name", 120))
                },
                "entitySetType" => new
                {
                    changed = catalog.Knowledge.SetType(fields.RequireGuid("entityId"), fields.RequireString("entityType", 32))
                },
                "entityMerge" => new
                {
                    changed = catalog.Knowledge.Merge(fields.RequireGuid("entityId"), fields.RequireGuid("targetEntityId"))
                },
                "entityReject" => new { changed = catalog.Knowledge.Reject(fields.RequireGuid("entityId")) },
                "entityRestore" => new { changed = catalog.Knowledge.Restore(fields.RequireGuid("entityId")) },
                "entityRelate" => new
                {
                    relationId = catalog.Knowledge.Relate(fields.RequireGuid("entityId"),
                        fields.RequireString("predicate", 32), fields.RequireGuid("targetEntityId"))
                },
                "verificationProviders" => new { providers = catalog.Verification.Providers(), discovered = ProviderModels.Discover() },
                "verificationProviderSave" => new { provider = SaveVerificationProvider(fields) },
                "verifications" => new { requests = catalog.Verification.List(fields.IntegerOrDefault("limit", 50, 1, 200)) },
                "verification" => new { request = catalog.Verification.Get(fields.RequireGuid("verificationId")) },
                "verificationPlan" => new { planned = catalog.Verification.Plan() },
                "verificationRequestSource" => new { verificationId = catalog.Verification.RequestForSource(fields.RequireGuid("sourceId")) },
                "verificationRequestEntity" => new { verificationId = catalog.Verification.RequestForEntity(fields.RequireGuid("entityId")) },
                "verificationApprove" => new { request = ApproveVerification(fields) },
                "verificationDecline" => new { declined = catalog.Verification.Decline(fields.RequireGuid("verificationId")) },
                "modelProfile" => new { profile = catalog.Models.Get(ModelProfile.LocalEnrichment) },
                "modelProfileSave" => new { profile = SaveModelProfile(fields, ModelProfile.LocalEnrichment) },
                "embeddingProfile" => new { profile = catalog.Models.Get(ModelProfile.LocalEmbedding) },
                "embeddingProfileSave" => new { profile = SaveModelProfile(fields, ModelProfile.LocalEmbedding) },
                "localModels" => new { models = await ListLocalModelsAsync(fields, cancellationToken) },
                "revoke" => new
                {
                    revoked = catalog.Sources.Revoke(fields.RequireGuid("sourceId"))
                },
                "stop" => Stop(),
                _ => throw new ArgumentException("The desktop action is not supported.")
            };
            return JsonSerializer.Serialize(new { requestId, ok = true, data = result },
                JsonOptions);
        }
        catch (Exception exception) when (exception is ArgumentException
            or JsonException or InvalidOperationException or IOException
            or InvalidDataException or UnauthorizedAccessException
            or KeyNotFoundException or NotSupportedException or InferenceException)
        {
            var failure = new
            {
                requestId,
                ok = false,
                error = exception.Message
            };
            return JsonSerializer.Serialize(failure, JsonOptions);
        }
    }

    private LocalWatchedIntake RequireMonitor() => monitor ?? throw new InvalidOperationException("Supervised intake is not configured.");

    private LocalWatchedIntakeGroup RequireGroup() => group ?? throw new InvalidOperationException("Folder reapproval requires supervised intake.");

    private LocalIntakeStatus IntakeStatus() => group?.Snapshot() ?? RequireMonitor().Snapshot();

    private LocalIntakeStatus PauseIntake(bool paused) => group?.SetPaused(paused) ?? RequireMonitor().SetPaused(paused);

    private LocalIntakeStatus RescanIntake() => group?.RequestRescan() ?? RequireMonitor().RequestRescan();

    private object Preview(DesktopRequestFields fields)
    {
        Guid itemId = fields.RequireGuid("itemId");
        int revision = fields.IntegerOrDefault("revision", 1, 1, int.MaxValue);
        TextDerivative? derivative = catalog.Text.Read(itemId, revision);
        return derivative is null
            ? new { outcome = "unavailable", content = (string?)null, reasonCode = (string?)null }
            : new
            {
                outcome = derivative.Outcome,
                content = derivative.Content is { } text ? text[..Math.Min(text.Length, 12_000)] : null,
                reasonCode = derivative.ReasonCode
            };
    }

    private async Task<object> VerifyAsync(DesktopRequestFields fields, CancellationToken cancellationToken)
    {
        await catalog.Originals.VerifyAsync(fields.RequireGuid("itemId"), cancellationToken);
        return new { verified = true };
    }

    private async Task<object> CaptureNoteAsync(DesktopRequestFields fields, CancellationToken cancellationToken)
    {
        group?.EnsurePrimaryRootApproved();
        LocalQuickCaptureReceipt capture = await (quickCapture ?? throw new InvalidOperationException("Quick capture is not configured."))
            .CaptureAsync(fields.RequireGuid("captureId"), fields.OptionalText("title", 80, "The note title is invalid."),
                fields.RequireString("text", 8_000), cancellationToken);
        processing?.Wake();
        return new { capture };
    }

    private ModelProfile SaveModelProfile(DesktopRequestFields fields, string role)
    {
        ModelProfile saved = catalog.Models.Save(new ModelProfile(
            role,
            fields.RequireString("provider", 32),
            fields.RequireString("endpoint", 256),
            fields.RequireString("model", 200),
            fields.IntegerOrDefault("contextTokens", 16_384, 2_048, 262_144),
            fields.RequireBoolean("enabled")));
        // A model change applies to the next job; waking clears any runtime backoff.
        processing?.Wake();
        return saved;
    }

    private async Task<object> SearchAsync(DesktopRequestFields fields, CancellationToken cancellationToken)
    {
        string query = fields.RequireString("query", 256);
        ModelProfile? profile = catalog.Models.Get(ModelProfile.LocalEmbedding);
        if (profile is not { Enabled: true })
        {
            return new { hits = catalog.Search.Query(query, 30), mode = "lexical", notice = (string?)null };
        }

        try
        {
            using OllamaRuntime runtime = new(profile.Endpoint, TimeSpan.FromSeconds(30));
            HybridSearchResults result = await catalog.Hybrid.QueryAsync(
                query, runtime.CreateEmbeddingModel(profile.Model), 30, cancellationToken);
            return new
            {
                hits = result.Hits,
                mode = result.UsedEmbeddings ? "hybrid" : "lexical",
                notice = result.UsedEmbeddings ? null : "No indexed embeddings for this model. Update the local semantic index to enable hybrid results."
            };
        }
        catch (InferenceException) when (!cancellationToken.IsCancellationRequested)
        {
            return new
            {
                hits = catalog.Search.Query(query, 30),
                mode = "lexical",
                notice = "The local embedding runtime is unavailable; showing keyword results."
            };
        }
    }

    private async Task<object> RefreshSemanticIndexAsync(DesktopRequestFields fields, CancellationToken cancellationToken)
    {
        ModelProfile profile = catalog.Models.Get(ModelProfile.LocalEmbedding)
            ?? throw new InvalidOperationException("Configure a local embedding model first.");
        if (!profile.Enabled)
        {
            throw new InvalidOperationException("Enable local embeddings before updating the index.");
        }

        using OllamaRuntime runtime = new(profile.Endpoint);
        SemanticIndexUpdate updated = await catalog.Embeddings.RefreshAsync(
            runtime.CreateEmbeddingModel(profile.Model), fields.RequireBoolean("rebuild"),
            maxSegments: 512, cancellationToken);
        return new { index = updated };
    }

    private static async Task<IReadOnlyList<InstalledModel>> ListLocalModelsAsync(
        DesktopRequestFields fields, CancellationToken cancellationToken)
    {
        using OllamaRuntime runtime = new OllamaRuntime(fields.RequireString("endpoint", 256), TimeSpan.FromSeconds(10));
        return await runtime.ListModelsAsync(cancellationToken);
    }

    private VerificationProvider SaveVerificationProvider(DesktopRequestFields fields)
    {
        VerificationProvider saved = catalog.Verification.SaveProvider(new VerificationProvider(
            fields.RequireString("providerId", 32),
            fields.RequireString("executable", 1024),
            fields.OptionalText("model", 100, "The provider model is invalid."),
            fields.IntegerOrDefault("costWeight", 10, 0, 1000),
            fields.IntegerOrDefault("dailyLimit", 20, 1, 500),
            fields.RequireBoolean("enabled")));
        verification?.Wake();
        return saved;
    }

    /// <summary>
    /// Approves exactly the reviewed payload: excluded items are removed and phrases redacted
    /// before anything can be sent; the runner is woken to send it.
    /// </summary>
    private VerificationRequestView ApproveVerification(DesktopRequestFields fields)
    {
        VerificationRequestView approved = catalog.Verification.Approve(
            fields.RequireGuid("verificationId"),
            fields.OptionalText("providerId", 32, "The provider is invalid."),
            fields.OptionalIntegers("excludedOrdinals", 1, LocalVerification.MaximumItemsPerRequest, LocalVerification.MaximumItemsPerRequest),
            fields.OptionalStrings("redactions", 200, 50));
        verification?.Wake();
        return approved;
    }

    private object Retry(DesktopRequestFields fields)
    {
        bool queued = catalog.Processing.Retry(fields.RequireGuid("itemId"), fields.IntegerOrDefault("revision", 1, 1, int.MaxValue), LocalProcessors.SourceText);
        if (queued)
        {
            processing?.Wake();
        }

        return new { queued };
    }

    private object Stop()
    {
        StopRequested = true;
        return new { stopping = true };
    }
}
