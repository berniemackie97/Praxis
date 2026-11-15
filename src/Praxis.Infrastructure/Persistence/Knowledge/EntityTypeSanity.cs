using Praxis.Domain.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

/// <summary>
/// SQL predicates over an entity's stored name that correct systematic model labeling mistakes
/// without rewriting proposals: work-item references the model calls projects, and runtime glue
/// that would otherwise dominate hub rankings. Owner and verified decisions always prevail.
/// </summary>
internal static class EntityTypeSanity
{
    /// <summary>Bump whenever a correction rule changes, so reconsolidation re-examines earlier judgments.</summary>
    internal const int Version = 1;

    private static readonly string GlueList = string.Join(", ",
        RuntimeGlue.Names.Order(StringComparer.Ordinal).Select(name => "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'"));

    /// <summary>
    /// True for ticket, issue or pull-request references such as <c>AUTH-001B</c>, <c>OCC #280</c>
    /// or <c>owner/4</c>, which a model often labels as projects. They are tracked work, not products.
    /// </summary>
    internal static string WorkItemName(string alias) => $"""
        (({alias}.display_name GLOB '[A-Z]*-*[0-9]*' AND {alias}.display_name NOT GLOB '*[^A-Z0-9-]*')
         OR {alias}.display_name GLOB '*#[0-9]*'
         OR ({alias}.display_name GLOB '*/[0-9]*' AND {alias}.display_name NOT GLOB '*/*[^0-9]*'))
        """;

    /// <summary>The SQL form of <see cref="RuntimeGlue.IsGlue"/> for the entity aliased <paramref name="alias"/>.</summary>
    internal static string RuntimeGlueName(string alias) => $"""
        ({alias}.display_name IN ({GlueList})
         OR {alias}.display_name GLOB '__?*' OR {alias}.display_name GLOB '_[A-Z]?*')
        """;

    /// <summary>
    /// The project type demoted to <c>other</c> when the name is a work-item reference; any other
    /// type passes through. <paramref name="type"/> is the SQL expression of the model-inferred type.
    /// </summary>
    internal static string CorrectInferred(string type, string alias) =>
        $"CASE WHEN {type} = 'project' AND {WorkItemName(alias)} THEN 'other' ELSE {type} END";
}
