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
            verification.ListProviders(), verifierModels.DiscoverExecutables())),
        DesktopAction.Of("verificationProviderSave", SaveProvider),
        DesktopAction.Of("verifications", fields => new VerificationRequestsResponse(
            verification.List(fields.IntegerOrDefault("limit", 50, 1, 200)))),
        DesktopAction.Of("verification", fields => new VerificationRequestResponse(
            verification.Get(fields.RequireGuid("verificationId")))),
        DesktopAction.Of("verificationPlan", _ => new VerificationPlanResponse(verification.Plan())),
        DesktopAction.Of("verificationRequestSource", fields => new VerificationCreatedResponse(
            verification.RequestForSource(fields.RequireGuid("sourceId")))),
        DesktopAction.Of("verificationRequestEntity", fields => new VerificationCreatedResponse(
            verification.RequestForEntity(fields.RequireGuid("entityId")))),
        DesktopAction.Of("verificationApprove", Approve),
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

    private VerificationRequestResponse Approve(DesktopRequestFields fields)
    {
        VerificationRequestView approved = approval.Approve(
            fields.RequireGuid("verificationId"),
            fields.OptionalText("providerId", 32, "The provider is invalid."),
            fields.OptionalIntegers("excludedOrdinals", 1, VerificationLimits.MaximumItemsPerRequest, VerificationLimits.MaximumItemsPerRequest),
            fields.OptionalStrings("redactions", 200, 50));
        runner?.Wake();
        return new VerificationRequestResponse(approved);
    }
}
