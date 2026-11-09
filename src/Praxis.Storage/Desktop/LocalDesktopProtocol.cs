using System.Text.Json;

namespace Praxis.Storage;

public sealed class LocalDesktopProtocol(
    LocalCatalog catalog, LocalQuickCapture? quickCapture = null,
    LocalWatchedIntake? monitor = null, LocalProcessingPipeline? processing = null)
{
    public const int MaximumRequestCharacters = 16_384;

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

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

            using var document = JsonDocument.Parse(request);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("A request object is required.");
            }

            var fields = new DesktopRequestFields(root);
            requestId = fields.RequireString("requestId", 64);
            var action = fields.RequireString("action", 32);
            object result = action switch
            {
                "library" => new
                {
                    items = catalog.Library.List(fields.IntegerOrDefault("limit", 100, 1, 200),
                        fields.IntegerOrDefault("offset", 0, 0, 1_000_000),
                        fields.OptionalGuid("collectionId"))
                },
                "search" => new
                {
                    hits = catalog.Search.Query(fields.RequireString("query", 256), 30)
                },
                "mediaCues" => new
                {
                    clips = catalog.Media.SearchCues(
                        fields.RequireString("query", 256),
                        fields.IntegerOrDefault("limit", 20, 1, 50))
                },
                "mediaInspection" => new
                {
                    media = await catalog.Media.InspectAsync(
                        fields.RequireGuid("sourceId"),
                        fields.OptionalInteger("revision", 1, int.MaxValue),
                        cancellationToken)
                },
                "budgetSummary" => new
                {
                    budget = await catalog.Finance.AnalyzeAsync(
                        fields.RequireGuid("sourceId"),
                        fields.IntegerOrDefault("year", 0, 2000, 2100),
                        fields.IntegerOrDefault("month", 0, 1, 12),
                        cancellationToken)
                },
                "budgetScenario" => new
                {
                    scenario = await catalog.Finance.PreviewAsync(
                        fields.RequireGuid("sourceId"),
                        fields.IntegerOrDefault("year", 0, 2000, 2100),
                        fields.IntegerOrDefault("month", 0, 1, 12),
                        fields.BudgetTargets(), cancellationToken)
                },
                "collections" => new
                {
                    collections = catalog.Collections.List(
                        fields.IntegerOrDefault("limit", 100, 1, 200),
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
                    changed = catalog.Collections.AddSource(
                        fields.RequireGuid("collectionId"), fields.RequireGuid("sourceId"))
                },
                "collectionRemove" => new
                {
                    changed = catalog.Collections.RemoveSource(
                        fields.RequireGuid("collectionId"), fields.RequireGuid("sourceId"))
                },
                "sourceCollections" => new
                {
                    collectionIds = catalog.Collections.ListForSource(
                        fields.RequireGuid("sourceId"))
                },
                "sourceMoves" => new
                {
                    moves = catalog.Sources.ListMovements(
                        fields.RequireGuid("sourceId"), fields.IntegerOrDefault("limit", 20, 1, 100))
                },
                "noteCapture" => await CaptureNoteAsync(fields, cancellationToken),
                "topics" => new
                {
                    proposals = catalog.Topics.List(
                        fields.RequireGuid("sourceId"), fields.IntegerOrDefault("limit", 40, 1, 100)),
                    outcome = catalog.Topics.Outcome(fields.RequireGuid("sourceId"))
                },
                "topicReview" => new
                {
                    reviewed = catalog.Topics.Review(
                        fields.RequireGuid("proposalId"),
                        fields.RequireString("decision", 16),
                        fields.OptionalText("correction", 80, "The review correction is invalid."))
                },
                "exportOriginal" => new
                {
                    exported = await catalog.Originals.ExportAsync(
                        fields.RequireGuid("itemId"),
                        fields.IntegerOrDefault("revision", 1, 1, int.MaxValue),
                        fields.RequireString("destinationPath", 4_096),
                        cancellationToken)
                },
                "intakeStatus" => new
                {
                    status = RequireMonitor().Snapshot()
                },
                "intakePause" => new
                {
                    status = RequireMonitor().SetPaused(fields.RequireBoolean("paused"))
                },
                "intakeRescan" => new
                {
                    status = RequireMonitor().RequestRescan()
                },
                "preview" => Preview(fields),
                "verify" => await VerifyAsync(fields, cancellationToken),
                "retry" => Retry(fields),
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
            or KeyNotFoundException or NotSupportedException)
        {
            var failure = new { requestId, ok = false, error = exception.Message };
            return JsonSerializer.Serialize(failure, JsonOptions);
        }
    }

    private LocalWatchedIntake RequireMonitor() =>
        monitor ?? throw new InvalidOperationException("Supervised intake is not configured.");

    private object Preview(DesktopRequestFields fields)
    {
        var itemId = fields.RequireGuid("itemId");
        var revision = fields.IntegerOrDefault("revision", 1, 1, int.MaxValue);
        var derivative = catalog.Text.Read(itemId, revision);
        return derivative is null
            ? new { outcome = "unavailable", content = (string?)null, reasonCode = (string?)null }
            : new
            {
                outcome = derivative.Outcome,
                content = derivative.Content is { } text
                    ? text[..Math.Min(text.Length, 12_000)] : null,
                reasonCode = derivative.ReasonCode
            };
    }

    private async Task<object> VerifyAsync(DesktopRequestFields fields, CancellationToken cancellationToken)
    {
        await catalog.Originals.VerifyAsync(fields.RequireGuid("itemId"), cancellationToken);
        return new { verified = true };
    }

    private async Task<object> CaptureNoteAsync(
        DesktopRequestFields fields, CancellationToken cancellationToken)
    {
        var capture = await (quickCapture ??
            throw new InvalidOperationException("Quick capture is not configured."))
            .CaptureAsync(fields.RequireGuid("captureId"),
                fields.OptionalText("title", 80, "The note title is invalid."),
                fields.RequireString("text", 8_000),
                cancellationToken);
        processing?.Wake();
        return new { capture };
    }

    private object Retry(DesktopRequestFields fields)
    {
        var queued = catalog.Processing.Retry(fields.RequireGuid("itemId"),
            fields.IntegerOrDefault("revision", 1, 1, int.MaxValue),
            LocalProcessors.SourceText);
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
