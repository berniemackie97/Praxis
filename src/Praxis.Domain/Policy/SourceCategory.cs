namespace Praxis.Domain.Policy;

/// <summary>
/// A source's data category, who decided it (<c>hint</c>, <c>model</c> or <c>owner</c>), and
/// whether the owner pinned it local-only (never disclosed remotely, ADR 0003).
/// </summary>
public sealed record SourceCategory(Guid SourceId, string Category, string Origin, bool LocalOnly)
{
    public const string HintOrigin = "hint";
    public const string ModelOrigin = "model";
    public const string OwnerOrigin = "owner";
}
