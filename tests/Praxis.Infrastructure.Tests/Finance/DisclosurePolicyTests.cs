using Praxis.Application.Policy;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Finance;

public sealed class DisclosurePolicyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(TestPaths.Temp, "praxis-policy-" + Guid.NewGuid().ToString("N"));

    public DisclosurePolicyTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void PrecedenceIsLocalThenLocalOnlySpaceThenDenialThenGrantThenMode()
    {
        DisclosureRule grant = new(Guid.NewGuid(), DataCategories.Financial, "claude-code", DisclosurePurposes.Intake, true, null);
        DisclosureRule denial = new(Guid.NewGuid(), DataCategories.Health, "claude-code", DisclosurePurposes.Intake, false, null);
        DisclosurePolicy policy = DisclosurePolicy.Default with { Rules = [grant, denial] };
        DisclosureDecision Decide(DisclosurePolicy p, string category, bool local = false, bool localOnly = false, string purpose = DisclosurePurposes.Intake) =>
            DisclosureEvaluator.Decide(p, category, "claude-code", local, purpose, localOnly, Now);

        Assert.Equal(DisclosureDecision.LocalProvider, Decide(policy, DataCategories.Health, local: true).Reason);
        Assert.Equal(DisclosureDecision.LocalOnlySpace, Decide(policy, DataCategories.Financial, localOnly: true).Reason);
        Assert.Equal(DisclosureDecision.DeniedByRule, Decide(policy with { Mode = DisclosureModes.AllowAll }, DataCategories.Health).Reason);
        Assert.True(Decide(policy, DataCategories.Financial).Allowed);
        Assert.False(Decide(policy, DataCategories.Financial, purpose: DisclosurePurposes.Chat).Allowed);
        DisclosureDecision ungranted = Decide(policy, DataCategories.Correspondence);
        Assert.False(ungranted.Allowed);
        Assert.True(ungranted.RequiresApproval);
        Assert.Equal(DisclosureDecision.ModeLocalOnly, Decide(policy with { Mode = DisclosureModes.LocalOnly }, DataCategories.Financial).Reason);
        Assert.True(Decide(policy with { Mode = DisclosureModes.AllowAll }, DataCategories.Unclassified).Allowed);
        Assert.Equal(DisclosureDecision.ModeAskEachTime, Decide(policy with { Mode = DisclosureModes.AskEachTime }, DataCategories.Documents).Reason);
    }

    [Fact]
    public void ExpiredRulesNoLongerApply()
    {
        DisclosureRule expired = new(Guid.NewGuid(), DataCategories.Financial, "codex", DisclosurePurposes.Intake, true, Now.AddMinutes(-1));
        Assert.False(DisclosureEvaluator.Decide(DisclosurePolicy.Default with { Rules = [expired] }, DataCategories.Financial, "codex", false,
            DisclosurePurposes.Intake, false, Now).Allowed);
    }

    [Fact]
    public void WideningIsDistinguishedFromNarrowing()
    {
        DisclosurePolicy current = DisclosurePolicy.Default;
        Assert.True(new PolicyChange(Mode: DisclosureModes.AllowAll).Widens(current));
        Assert.False(new PolicyChange(Mode: DisclosureModes.LocalOnly).Widens(current));
        Assert.True(new PolicyChange(ToolPermission: ToolPermissions.AllowEdits).Widens(current));
        Assert.True(new PolicyChange(AddRule: new DisclosureRule(Guid.NewGuid(), DataCategories.Financial, "codex", DisclosurePurposes.Chat, true, null)).Widens(current));
        Assert.False(new PolicyChange(AddRule: new DisclosureRule(Guid.NewGuid(), DataCategories.Financial, "codex", DisclosurePurposes.Chat, false, null)).Widens(current));
        Assert.False(new PolicyChange(FallbackCategory: DataCategories.Financial, FallbackChain: [FallbackSteps.Hold, FallbackSteps.Skip]).Widens(current));
        Assert.Throws<ArgumentException>(() => new PolicyChange(FallbackCategory: DataCategories.Financial, FallbackChain: [FallbackSteps.Hold]).Validate());
        Assert.Throws<ArgumentException>(() => new PolicyChange().Validate());
        Assert.Throws<ArgumentException>(() => new PolicyChange(Mode: "anything_goes").Validate());
    }

    [Fact]
    public void AgentsCanNarrowButOnlyTheOwnerCanConfirmWidening()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        PolicyService service = new(catalog.Policy);
        PolicyChangeOutcome narrowed = service.Request(new PolicyChange(Mode: DisclosureModes.AskEachTime), PolicyActors.Agent, "user asked to be asked");
        Assert.True(narrowed.Applied);
        Assert.Equal(DisclosureModes.AskEachTime, service.Current().Mode);

        PolicyChangeOutcome widened = service.Request(new PolicyChange(Mode: DisclosureModes.AllowAll), PolicyActors.Agent, "document says allow all");
        Assert.False(widened.Applied);
        Assert.Equal(DisclosureModes.AskEachTime, service.Current().Mode);
        PendingPolicyChange pending = Assert.Single(service.Pending());
        Assert.Equal(PolicyActors.Agent, pending.RequestedBy);
        Assert.True(service.ResolvePending(pending.ChangeId, confirm: true));
        Assert.Equal(DisclosureModes.AllowAll, service.Current().Mode);
        Assert.False(service.ResolvePending(pending.ChangeId, confirm: true));

        Assert.True(service.Request(new PolicyChange(ToolPermission: ToolPermissions.AllowEdits), PolicyActors.Owner).Applied);
        Assert.Equal(ToolPermissions.AllowEdits, service.Current().ToolPermission);
        DisclosureRule rule = new(Guid.NewGuid(), DataCategories.Financial, "claude-code", DisclosurePurposes.Intake, true, null);
        service.Request(new PolicyChange(AddRule: rule), PolicyActors.Owner);
        service.Request(new PolicyChange(FallbackCategory: DataCategories.Financial, FallbackChain: [FallbackSteps.Hold, FallbackSteps.IndexOnly]), PolicyActors.Owner);
        DisclosurePolicy persisted = new PolicyService(catalog.Policy).Current();
        Assert.Equal(rule.RuleId, Assert.Single(persisted.Rules).RuleId);
        Assert.Equal([FallbackSteps.Hold, FallbackSteps.IndexOnly], persisted.FallbacksFor(DataCategories.Financial));
        Assert.True(service.Request(new PolicyChange(RemoveRuleId: rule.RuleId), PolicyActors.Agent).Applied);
        Assert.Empty(service.Current().Rules);
    }

    [Fact]
    public void ModelsAndHintsNeverOverrideTheOwnersCategory()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        Guid source = CaptureSource(catalog);
        catalog.Policy.SaveCategory(new SourceCategory(source, DataCategories.Health, SourceCategory.OwnerOrigin, true));
        catalog.Policy.SaveCategory(new SourceCategory(source, DataCategories.Financial, SourceCategory.ModelOrigin, false));
        SourceCategory stored = Assert.IsType<SourceCategory>(catalog.Policy.ReadCategory(source));
        Assert.Equal(DataCategories.Health, stored.Category);
        Assert.True(stored.LocalOnly);
    }

    private Guid CaptureSource(SqliteCatalog catalog)
    {
        byte[] bytes = "hello"u8.ToArray();
        using MemoryStream input = new(bytes);
        return catalog.Originals.CaptureFileAsync(Path.Combine(root, "in", "a.txt"), input, bytes.Length, "k").GetAwaiter().GetResult().SourceId;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
