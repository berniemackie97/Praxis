namespace Praxis.Domain.Policy;

/// <summary>
/// The sensitivity category assigned to every source before any remote processing.
/// <see cref="Unclassified"/> is treated as the most sensitive category.
/// </summary>
public static class DataCategories
{
    public const string Financial = "financial";
    public const string Correspondence = "correspondence";
    public const string Health = "health";
    public const string Identity = "identity";
    public const string Personal = "personal";
    public const string Documents = "documents";
    public const string Media = "media";
    public const string Code = "code";
    public const string Unclassified = "unclassified";

    public static readonly IReadOnlyList<string> All =
        [Financial, Correspondence, Health, Identity, Personal, Documents, Media, Code, Unclassified];

    public static bool IsValid(string? category) => category is not null && All.Contains(category, StringComparer.Ordinal);
}
