namespace Praxis.Domain.Knowledge;

/// <summary>Closed values for knowledge revisions and the triggers that cause a second look.</summary>
public static class RevisionVocabulary
{
    /// <summary>A rule or prompt version changed, so earlier judgments may be stale.</summary>
    public const string Rules = "rules";

    /// <summary>A source was revoked, so its entities are known from less evidence.</summary>
    public const string Revocation = "revocation";

    /// <summary>New mentions or relations arrived for an entity.</summary>
    public const string Mentions = "mentions";

    /// <summary>The owner corrected an entity; look-alikes may share the mistake.</summary>
    public const string OwnerLesson = "owner_lesson";

    /// <summary>The entity's confirmed relations contradict its type.</summary>
    public const string Relations = "relations";

    /// <summary>Another entity has the same name apart from spacing, punctuation or case.</summary>
    public const string Duplicates = "duplicates";

    public const string EntityType = "entity_type";
    public const string Merge = "merge";
    public const string Relation = "relation";

    /// <summary>The proposed action for a relation that its endpoints' confirmed types rule out.</summary>
    public const string Retract = "retract";

    public const string Applied = "applied";
    public const string Suggested = "suggested";
    public const string Accepted = "accepted";
    public const string Dismissed = "dismissed";

    public static readonly IReadOnlySet<string> States = new HashSet<string>(StringComparer.Ordinal)
    {
        Applied, Suggested, Accepted, Dismissed
    };
}
