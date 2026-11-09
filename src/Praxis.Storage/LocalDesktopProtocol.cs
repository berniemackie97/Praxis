using System.Text.Json;

namespace Praxis.Storage;

public sealed class LocalDesktopProtocol(LocalCatalog catalog)
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
                        ReadInteger(root, "offset", 0, 0, 1_000_000))
                },
                "search" => new
                {
                    hits = catalog.SearchText(ReadString(root, "query", 256), 30)
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
            return JsonSerializer.Serialize(new
            {
                requestId, ok = false, error = exception.Message
            }, JsonOptions);
        }
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
