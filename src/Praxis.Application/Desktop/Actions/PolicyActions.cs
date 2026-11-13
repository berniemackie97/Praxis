using System.Globalization;
using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Policy;
using Praxis.Domain.Policy;

namespace Praxis.Application.Desktop.Actions;

/// <summary>
/// Owner controls for disclosure, tiers, categories and holds. These actions are reachable only
/// through the first-party private pipe, so they act as the owner; they are never registered as
/// agent tools. Agents request changes through <see cref="PolicyService.Request"/> as
/// <see cref="PolicyActors.Agent"/>, where widening changes wait for confirmation here.
/// </summary>
public sealed class PolicyActions(PolicyService policy, IPolicyRepository repository) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("policyGet", _ => new PolicyResponse(policy.Current(), repository.ReadTiers(), policy.Pending())),
        DesktopAction.Of("policyChange", fields =>
        {
            PolicyChangeOutcome outcome = policy.Request(Change(fields), PolicyActors.Owner);
            return new PolicyChangeResponse(outcome.Applied, outcome.PendingChangeId);
        }),
        DesktopAction.Of("policyResolve", fields => new ChangedResponse(policy.ResolvePending(fields.RequireGuid("changeId"), fields.RequireBoolean("confirm")))),
        DesktopAction.Of("disclosureHolds", fields => new DisclosureHoldsResponse(repository.ReadHolds(
            fields.OptionalText("scope", 8, "The scope field is invalid.") != "all", fields.IntegerOrDefault("limit", 100, 1, 500)))),
        DesktopAction.Of("disclosureHoldDecide", fields => new ChangedResponse(
            repository.DecideHold(fields.RequireGuid("holdId"), fields.RequireString("decision", 32)) is not null)),
        DesktopAction.Of("modelTierSet", fields =>
        {
            repository.SaveTier(fields.RequireString("tier", 16), fields.OptionalText("providerId", 64, "The providerId field is invalid."));
            return new ChangedResponse(true);
        }),
        DesktopAction.Of("sourceCategorySet", fields =>
        {
            repository.SaveCategory(new SourceCategory(fields.RequireGuid("sourceId"), fields.RequireString("category", 32),
                SourceCategory.OwnerOrigin, fields.RequireBoolean("localOnly")));
            return new ChangedResponse(true);
        }),
        DesktopAction.Of("modelInvocations", fields => new ModelInvocationsResponse(repository.ReadInvocations(fields.IntegerOrDefault("limit", 100, 1, 1_000))))
    ];

    private static PolicyChange Change(DesktopRequestFields fields)
    {
        string kind = fields.RequireString("change", 24);
        return kind switch
        {
            "mode" => new PolicyChange(Mode: fields.RequireString("mode", 32)),
            "toolPermission" => new PolicyChange(ToolPermission: fields.RequireString("toolPermission", 32)),
            "removeRule" => new PolicyChange(RemoveRuleId: fields.RequireGuid("ruleId")),
            "fallback" => new PolicyChange(FallbackCategory: fields.RequireString("category", 32),
                FallbackChain: fields.RequireString("chain", 128).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)),
            "addRule" => new PolicyChange(AddRule: new DisclosureRule(Guid.NewGuid(), fields.RequireString("category", 32),
                fields.RequireString("providerId", 64), fields.RequireString("purpose", 16), fields.RequireBoolean("allow"),
                fields.OptionalText("expiresUtc", 40, "The expiresUtc field is invalid.") is { } expires
                    ? DateTimeOffset.Parse(expires, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null)),
            _ => throw new ArgumentException("The change field is not recognized.")
        };
    }
}
