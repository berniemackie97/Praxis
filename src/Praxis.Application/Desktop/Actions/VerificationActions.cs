using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Verification;
using Praxis.Domain.Verification;

namespace Praxis.Application.Desktop.Actions;

/// <summary>
/// Owner-approved verification by stronger models: provider setup, request review, approval
/// of exactly the reviewed payload, and decline. Approvals and provider changes wake the runner.
/// </summary>
public sealed class VerificationActions(
    IVerificationRepository verification, VerificationApproval approval, VerificationProviderConfiguration providers,
    IVerifierModelFactory verifierModels, VerificationRunner? runner = null) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("verificationProviders", _ => new VerificationProvidersResponse(
            verification.ListProviders(), verifierModels.DiscoverExecutables(),
            verification.ListEnabledProviderAllowances())),
        DesktopAction.Of("verificationProviderSave", SaveProvider),
        DesktopAction.Of("verifications", fields => new VerificationRequestsResponse(
            verification.List(fields.IntegerOrDefault("limit", 50, 1, 200), fields.OptionalBoolean("summariesOnly") ?? false))),
        DesktopAction.Of("verification", fields => new VerificationRequestResponse(
            verification.Get(fields.RequireGuid("verificationId")))),
        DesktopAction.Of("verificationPlan", _ => new VerificationPlanResponse(verification.Plan())),
        DesktopAction.Of("verificationRequestSource", fields => new VerificationCreatedResponse(
            verification.RequestForSource(fields.RequireGuid("sourceId")))),
        DesktopAction.Of("verificationRequestEntity", fields => new VerificationCreatedResponse(
            verification.RequestForEntity(fields.RequireGuid("entityId")))),
        DesktopAction.Of("verificationDisclosureSources", fields => new VerificationDisclosureSourcesResponse(
            verification.DisclosureSources(fields.RequireGuid("verificationId")))),
        DesktopAction.Of("verificationSourceDisclosureSet", fields =>
            new VerificationDisclosurePolicyResponse(verification.SetSourceRemoteVerification(
                fields.RequireGuid("sourceId"), fields.RequireBoolean("allowRemoteVerification")))),
        DesktopAction.Of("verificationDisclosureRevoke", fields =>
            new VerificationDisclosureRevokedResponse(verification.RevokeDisclosure(
                fields.RequireGuid("verificationId")))),
        DesktopAction.Of("verificationDisclosureAudit", fields =>
            new VerificationDisclosureAuditResponse(verification.DisclosureAudit(
                fields.IntegerOrDefault("limit", 50, 1, 200)))),
        DesktopAction.Of("verificationApprove", Approve),
        DesktopAction.Of("verificationApproveLocalBatch", ApproveLocalBatch),
        DesktopAction.Of("verificationDecline", fields => new VerificationDeclineResponse(
            verification.Decline(fields.RequireGuid("verificationId"))))
    ];

    private VerificationProviderResponse SaveProvider(DesktopRequestFields fields)
    {
        string id = fields.RequireString("providerId", 32);
        VerificationProvider saved = providers.Save(new VerificationProvider(
            id,
            id == VerificationProvider.Ollama ? null : fields.RequireString("executable", 1024),
            fields.OptionalText("model", 100, "The provider model is invalid."),
            fields.IntegerOrDefault("costWeight", 10, 0, 1000),
            fields.IntegerOrDefault("dailyLimit", 20, 1, 500),
            fields.RequireBoolean("enabled"),
            id == VerificationProvider.Ollama ? fields.RequireString("endpoint", 256) : null));
        runner?.Wake();
        return new VerificationProviderResponse(saved);
    }

    private VerificationBatchApprovalResponse ApproveLocalBatch(DesktopRequestFields fields)
    {
        Guid[] ids = fields.OptionalStrings("verificationIds", 36, VerificationLimits.MaximumAwaitingRequests)
            .Select(id => Guid.TryParseExact(id, "D", out Guid parsed) && parsed != Guid.Empty
                ? parsed : throw new ArgumentException("Each request needs a valid identifier."))
            .Distinct().ToArray();
        if (ids.Length == 0)
        {
            throw new ArgumentException("No requests selected.");
        }

        if (VerificationRouter.Route(verification.ListEnabledProviderAllowances(), VerificationProvider.Ollama) is null)
        {
            throw new InvalidOperationException("Enable local Ollama under Models before approving a batch.");
        }

        List<Guid> queued = [];
        List<Guid> skipped = [];
        foreach (Guid id in ids)
        {
            try
            {
                approval.Approve(id, VerificationProvider.Ollama);
                queued.Add(id);
            }
            catch (InvalidOperationException)
            {
                skipped.Add(id);
            }
        }

        if (queued.Count > 0)
        {
            runner?.Wake();
        }

        return new VerificationBatchApprovalResponse(queued, skipped);
    }

    private VerificationRequestResponse Approve(DesktopRequestFields fields)
    {
        VerificationRequestView approved = approval.Approve(
            fields.RequireGuid("verificationId"),
            fields.OptionalText("providerId", 32, "The provider is invalid."),
            fields.OptionalIntegers("excludedOrdinals", 1, VerificationLimits.MaximumItemsPerRequest, VerificationLimits.MaximumItemsPerRequest),
            fields.OptionalStrings("redactions", 200, 50),
            fields.OptionalBoolean("remoteDisclosureApproved") ?? false, fields.OptionalInteger("providerRevision", 1, int.MaxValue));
        runner?.Wake();
        return new VerificationRequestResponse(approved);
    }
}
