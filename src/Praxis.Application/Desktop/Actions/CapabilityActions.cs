using Praxis.Application.Agents.Capabilities;

namespace Praxis.Application.Desktop.Actions;

/// <summary>The owner, not an agent tool, can approve or reject actions through the private desktop pipe.</summary>
public sealed class CapabilityActions(CapabilityService capabilities) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("capabilityRequests", fields =>
            new CapabilityRequestsResponse(capabilities.List(fields.IntegerOrDefault("limit", 50, 1, 100)))),
        DesktopAction.OfAsync("capabilityDecide", async (fields, cancellationToken) =>
            await capabilities.ResolveAsync(fields.RequireGuid("id"), fields.RequireBoolean("accept"), cancellationToken)
                ?? throw new InvalidOperationException("This request was already decided or has expired."))
    ];

    private sealed record CapabilityRequestsResponse(IReadOnlyList<CapabilityRequest> Requests);
}
