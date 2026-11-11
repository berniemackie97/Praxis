using System.Text.Json;
using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Inference;

namespace Praxis.Application.Desktop;

/// <summary>
/// The owner-local desktop protocol: one JSON request object per line in, one JSON response
/// object per line out. Requests are bounded, every field is validated, and only expected
/// failures are reported back, by message; anything else is a worker fault.
/// </summary>
public sealed class DesktopProtocol
{
    /// <summary>The protocol version this worker speaks; a request may name it in <c>protocolVersion</c>.</summary>
    public const int Version = 1;

    public const int MaximumRequestCharacters = 16_384;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, DesktopAction> actions;

    public DesktopProtocol(IEnumerable<IDesktopActionGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        Dictionary<string, DesktopAction> registered = new(StringComparer.Ordinal);
        foreach (DesktopAction action in groups.SelectMany(group => group.Actions).Append(DesktopAction.Of("stop", _ => Stop())))
        {
            if (!registered.TryAdd(action.Name, action))
            {
                throw new ArgumentException($"The desktop action '{action.Name}' is registered twice.", nameof(groups));
            }
        }

        actions = registered;
    }

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
            string name = fields.RequireString("action", 32);
            if (fields.OptionalInteger("protocolVersion", 1, int.MaxValue) is { } version && version != Version)
            {
                throw new NotSupportedException("The desktop protocol version is not supported.");
            }

            DesktopAction action = actions.TryGetValue(name, out DesktopAction? found)
                ? found
                : throw new ArgumentException("The desktop action is not supported.");
            object data = await action.ExecuteAsync(fields, cancellationToken);
            return JsonSerializer.Serialize(new DesktopSuccess(requestId, true, data), JsonOptions);
        }
        catch (Exception exception) when (exception is ArgumentException
            or JsonException or InvalidOperationException or IOException
            or InvalidDataException or UnauthorizedAccessException
            or KeyNotFoundException or NotSupportedException or InferenceException)
        {
            return JsonSerializer.Serialize(new DesktopFailure(requestId, false, exception.Message), JsonOptions);
        }
    }

    private StopResponse Stop()
    {
        StopRequested = true;
        return new StopResponse(true);
    }

    private sealed record DesktopSuccess(string RequestId, bool Ok, object Data);

    private sealed record DesktopFailure(string? RequestId, bool Ok, string Error);
}
