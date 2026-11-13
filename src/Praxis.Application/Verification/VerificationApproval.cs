using Praxis.Domain.Verification;

namespace Praxis.Application.Verification;

/// <summary>
/// Approves exactly the payload the owner reviewed: excluded items are removed and phrases
/// redacted before anything can be sent, and the request is routed to a provider with allowance.
/// </summary>
public sealed class VerificationApproval(IVerificationRepository repository)
{
    private const string NotAwaitingApproval = "Only a request awaiting approval can be approved.";

    public VerificationRequestView Approve(
        Guid requestId, string? providerId = null, IReadOnlyCollection<int>? excludedOrdinals = null,
        IReadOnlyCollection<string>? redactions = null, bool remoteDisclosureApproved = false)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("An identifier is required.", nameof(requestId));
        }

        string payload = repository.ReadAwaitingPayload(requestId)
            ?? throw new InvalidOperationException(NotAwaitingApproval);
        VerificationProvider provider = VerificationRouter.Route(repository.ListEnabledProviderAllowances(), providerId)
            ?? throw new InvalidOperationException(providerId is null
                ? "Enable a local Ollama verifier or explicitly select an external provider."
                : "The selected verifier is disabled or has reached its daily limit.");
        if (!provider.IsLocal && !remoteDisclosureApproved)
        {
            throw new InvalidOperationException("Explicit consent to disclose this payload to the selected provider is required.");
        }

        (string narrowed, IReadOnlySet<int> kept) = VerificationPayload.Narrow(payload, excludedOrdinals ?? [], redactions ?? []);
        return repository.Approve(requestId, payload, provider.ProviderId, provider.Revision, narrowed, kept, remoteDisclosureApproved)
            ?? throw new InvalidOperationException(NotAwaitingApproval);
    }
}
