using System.Text.Json;

namespace Praxis.Storage;

public sealed class LocalDesktopProtocol(
    LocalCatalog catalog, LocalQuickCapture? quickCapture = null,
    LocalWatchedIntake? monitor = null)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public bool StopRequested { get; private set; }

    public async Task<string> HandleAsync(string request, CancellationToken cancellationToken = default)
    {
        string? requestId = null;
        try
        {
            if (request.Length is < 2 or > 16_384)
            {
                throw new ArgumentException("The desktop request exceeds its size limit.");
            }

            using var document = JsonDocument.Parse(request);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("A request object is required.");
            }

            requestId = ReadString(root, "requestId", 64);
            var action = ReadString(root, "action", 32);
            object result = action switch
            {
                "library" => new
                {
                    items = catalog.ListLibrary(ReadInteger(root, "limit", 100, 1, 200),
                        ReadInteger(root, "offset", 0, 0, 1_000_000),
                        ReadOptionalGuid(root, "collectionId"))
                },
                "search" => new
                {
                    hits = catalog.SearchText(ReadString(root, "query", 256), 30)
                },
                "mediaCues" => new
                {
                    clips = catalog.SearchVideoCues(
                        ReadString(root, "query", 256),
                        ReadInteger(root, "limit", 20, 1, 50))
                },
                "budgetSummary" => new
                {
                    budget = await new LocalMonthlyBudgetAnalyzer(catalog).AnalyzeAsync(
                        ReadGuid(root, "sourceId"),
                        ReadInteger(root, "year", 0, 2000, 2100),
                        ReadInteger(root, "month", 0, 1, 12),
                        cancellationToken)
                },
                "collections" => new
                {
                    collections = catalog.ListCollections(
                        ReadInteger(root, "limit", 100, 1, 200),
                        ReadInteger(root, "offset", 0, 0, 1_000_000))
                },
                "collectionCreate" => new
                {
                    collection = catalog.CreateCollection(ReadString(root, "name", 80))
                },
                "collectionRename" => new
                {
                    changed = catalog.RenameCollection(ReadGuid(root, "collectionId"),
                        ReadString(root, "name", 80))
                },
                "collectionArchive" => new
                {
                    changed = catalog.ArchiveCollection(ReadGuid(root, "collectionId"))
                },
                "collectionAdd" => new
                {
                    changed = catalog.AddSourceToCollection(
                        ReadGuid(root, "collectionId"), ReadGuid(root, "sourceId"))
                },
                "collectionRemove" => new
                {
                    changed = catalog.RemoveSourceFromCollection(
                        ReadGuid(root, "collectionId"), ReadGuid(root, "sourceId"))
                },
                "sourceCollections" => new
                {
                    collectionIds = catalog.ListSourceCollectionIds(
                        ReadGuid(root, "sourceId"))
                },
                "sourceMoves" => new
                {
                    moves = catalog.ListSourceMovements(
                        ReadGuid(root, "sourceId"), ReadInteger(root, "limit", 20, 1, 100))
                },
                "noteCapture" => new
                {
                    capture = await (quickCapture ??
                        throw new InvalidOperationException("Quick capture is not configured."))
                        .CaptureAsync(ReadGuid(root, "captureId"),
                            ReadOptionalTitle(root), ReadString(root, "text", 8_000),
                            cancellationToken)
                },
                "topics" => new
                {
                    proposals = catalog.ListTopicProposals(
                        ReadGuid(root, "sourceId"), ReadInteger(root, "limit", 40, 1, 100)),
                    outcome = catalog.TopicProcessingOutcome(ReadGuid(root, "sourceId"))
                },
                "topicReview" => new
                {
                    reviewed = catalog.ReviewTopicProposal(
                        ReadGuid(root, "proposalId"),
                        ReadString(root, "decision", 16), ReadOptionalCorrection(root))
                },
                "exportOriginal" => new
                {
                    exported = await catalog.ExportOriginalAsync(
                        ReadGuid(root, "itemId"),
                        ReadInteger(root, "revision", 1, 1, int.MaxValue),
                        ReadString(root, "destinationPath", 4_096),
                        cancellationToken)
                },
                "intakeStatus" => new
                {
                    status = RequireMonitor().Snapshot()
                },
                "intakePause" => new
                {
                    status = RequireMonitor().SetPaused(ReadBoolean(root, "paused"))
                },
                "intakeRescan" => new
                {
                    status = RequireMonitor().RequestRescan()
                },
                "preview" => Preview(root),
                "verify" => await VerifyAsync(root, cancellationToken),
                "retry" => new
                {
                    queued = catalog.RetryProcessing(ReadGuid(root, "itemId"),
                        ReadInteger(root, "revision", 1, 1, int.MaxValue),
                        LocalCatalog.DefaultProcessorKey)
                },
                "revoke" => new
                {
                    revoked = catalog.RevokeSource(ReadGuid(root, "sourceId"))
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

    private static bool ReadBoolean(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ArgumentException($"The {property} field must be true or false.");
        }

        return value.GetBoolean();
    }

    private object Preview(JsonElement root)
    {
        var itemId = ReadGuid(root, "itemId");
        var revision = ReadInteger(root, "revision", 1, 1, int.MaxValue);
        var derivative = catalog.ReadTextDerivative(itemId, revision);
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

    private async Task<object> VerifyAsync(JsonElement root, CancellationToken cancellationToken)
    {
        await catalog.VerifyOriginalAsync(ReadGuid(root, "itemId"), cancellationToken);
        return new { verified = true };
    }

    private object Stop()
    {
        StopRequested = true;
        return new { stopping = true };
    }

    private static string? ReadOptionalCorrection(JsonElement root)
    {
        if (!root.TryGetProperty("correction", out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } correction || correction.Length > 80)
        {
            throw new ArgumentException("The review correction is invalid.");
        }

        return correction;
    }

    private static string? ReadOptionalTitle(JsonElement root)
    {
        if (!root.TryGetProperty("title", out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } title || title.Length > 80)
        {
            throw new ArgumentException("The note title is invalid.");
        }

        return title;
    }

    private static Guid? ReadOptionalGuid(JsonElement root, string property) =>
        root.TryGetProperty(property, out _) ? ReadGuid(root, property) : null;

    private static Guid ReadGuid(JsonElement root, string property)
    {
        var text = ReadString(root, property, 36);
        return Guid.TryParseExact(text, "D", out var id) && id != Guid.Empty
            ? id : throw new ArgumentException("A valid item or source identifier is required.");
    }

    private static string ReadString(JsonElement root, string property, int limit)
    {
        if (!root.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { } text ||
            text.Length is < 1 || text.Length > limit)
        {
            throw new ArgumentException($"The {property} field is invalid.");
        }

        return text;
    }

    private static int ReadInteger(
        JsonElement root, string property, int fallback, int min, int max)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number) || number < min || number > max)
        {
            throw new ArgumentException($"The {property} field is outside its limit.");
        }

        return number;
    }
}
