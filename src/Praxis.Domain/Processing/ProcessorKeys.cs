namespace Praxis.Domain.Processing;

/// <summary>
/// Versioned identities of the processors that produce catalog evidence, and the stage graph
/// that schedules a processor once the evidence it consumes has been committed.
/// </summary>
public static class ProcessorKeys
{
    public const string SourceText = "source-text-v1";
    public const string MarkdownHeadings = "markdown-heading-v1";
    public const string TextSegments = "text-segments-v1";
    public const string SemanticExtraction = "semantic-extract-v2";
    public const string MediaHeader = "media-header-v1";
    public const string TimedCaption = "timed-caption-v1";

    private static readonly IReadOnlyDictionary<string, string[]> Dependents =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [SourceText] = [MarkdownHeadings, TextSegments],
            [TextSegments] = [SemanticExtraction]
        };

    /// <summary>Processors that consume the completed output of <paramref name="processorKey"/>.</summary>
    public static IReadOnlyList<string> DependentsOf(string processorKey) =>
        Dependents.TryGetValue(processorKey, out string[]? dependents) ? dependents : [];
}
