namespace Praxis.Domain.Policy;

/// <summary>
/// Whether content of one category may be sent to one provider for one purpose. When not
/// allowed, <see cref="RequiresApproval"/> tells whether the mode asks for a per-item decision
/// (rather than an outright refusal); either way the category's fallback chain then applies.
/// </summary>
public sealed record DisclosureDecision(bool Allowed, bool RequiresApproval, string Reason, Guid? RuleId)
{
    public const string LocalProvider = "local_provider";
    public const string LocalOnlySpace = "local_only_space";
    public const string DeniedByRule = "denied_by_rule";
    public const string GrantedByRule = "granted_by_rule";
    public const string ModeLocalOnly = "mode_local_only";
    public const string ModeAskEachTime = "mode_ask_each_time";
    public const string NoGrant = "no_grant";
    public const string SourceChanged = "source_changed";
    public const string ModeAllowAll = "mode_allow_all";
}
