namespace Praxis.Domain.Policy;

/// <summary>Who asked for a policy change. Only the owner acting in the first-party UI can widen.</summary>
public static class PolicyActors
{
    public const string Owner = "owner";
    public const string Agent = "agent";
}
