using Praxis.Application.Inference;
using Praxis.Domain.Models;
using Praxis.Domain.Verification;

namespace Praxis.Application.Verification;

/// <summary>
/// Durable verification providers and requests. Request creation, leasing and completion are
/// atomic; leases fence a stale runner so it cannot record verdicts after losing its claim.
/// </summary>
public interface IVerificationRepository
{
    IReadOnlyList<VerificationProvider> ListProviders();

    /// <summary>
    /// Stores a provider. Saving is the owner's signal that the provider is ready again (for
    /// example after signing in), so approved requests waiting on it lose their retry delay.
    /// </summary>
    void SaveProvider(VerificationProvider provider);

    /// <summary>Enabled providers with the requests each has started since the start of the UTC day.</summary>
    IReadOnlyList<ProviderAllowance> ListEnabledProviderAllowances();

    /// <summary>
    /// Creates requests awaiting approval for unverified instruction-like proposals and for
    /// unresolved entity type conflicts, within <see cref="VerificationLimits"/>. Items already in
    /// a request (other than a failed one) are not planned again, so declining silences them.
    /// </summary>
    int Plan();

    /// <summary>Requests verification of a source's pending entity and relation proposals.</summary>
    Guid? RequestForSource(Guid sourceId);

    /// <summary>Requests that a stronger model settle an entity's type.</summary>
    Guid? RequestForEntity(Guid entityId);

    IReadOnlyList<VerificationRequestView> List(int limit, bool summariesOnly = false);

    VerificationRequestView? Get(Guid requestId);

    /// <summary>The payload of a request awaiting approval, or null when it is not awaiting approval.</summary>
    string? ReadAwaitingPayload(Guid requestId);

    /// <summary>
    /// Approves a request still awaiting approval with exactly <paramref name="reviewedPayload"/>,
    /// routes it to <paramref name="providerId"/> and records which item ordinals remain included.
    /// Returns null when the request is no longer awaiting approval with that payload.
    /// </summary>
    VerificationRequestView? Approve(
        Guid requestId, string reviewedPayload, string providerId, int providerRevision,
        string approvedPayload, IReadOnlySet<int> includedOrdinals, bool remoteDisclosureApproved = false);

    /// <summary>Source-level owner classification; absent rows are always local-only.</summary>
    IReadOnlyList<VerificationDisclosureSource> DisclosureSources(Guid requestId);
    bool SetSourceRemoteVerification(Guid sourceId, bool allowed);

    /// <summary>Immediately withdraws an external request's disclosure grant.</summary>
    bool RevokeDisclosure(Guid requestId);

    /// <summary>Rechecks the lease and all authorization immediately before any model payload is sent.</summary>
    bool CanDispatch(ClaimedVerification claim);

    /// <summary>Bounded content-free authorization decisions for first-party incident review.</summary>
    IReadOnlyList<VerificationDisclosureEvent> DisclosureAudit(int limit);

    bool Decline(Guid requestId);

    /// <summary>Leases the oldest approved request whose provider is enabled, not backed off and within allowance.</summary>
    ClaimedVerification? Claim();

    /// <summary>
    /// Records verdicts and their machine-layer effects atomically: item verdicts, and verified
    /// types for entities whose type the owner has not set.
    /// </summary>
    bool Complete(ClaimedVerification claim, ModelIdentity model, ChatResult result, IReadOnlyList<VerificationVerdict> verdicts);

    /// <summary>Returns a running request to the approved queue until <paramref name="retryAfter"/>.</summary>
    bool Release(ClaimedVerification claim, DateTimeOffset retryAfter, string failureCode);

    bool Fail(ClaimedVerification claim, string failureCode);
}
