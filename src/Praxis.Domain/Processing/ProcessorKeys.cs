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

    /// <summary>
    /// Recognizes bank and card documents (OFX/QFX/QBO, QIF, delimited exports, PDF statements)
    /// directly from verified originals. Scheduled at capture for candidate file extensions only.
    /// </summary>
    public const string FinancialRecords = "financial-records-v1";

    /// <summary>
    /// Local-model categories for merchants the deterministic rules leave uncategorized. Runs
    /// after <see cref="FinancialRecords"/> completes and is deferred while no model is enabled.
    /// </summary>
    public const string FinancialCategories = "financial-categories-v1";

    private static readonly IReadOnlyDictionary<string, string[]> Dependents =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [SourceText] = [MarkdownHeadings, TextSegments],
            [TextSegments] = [SemanticExtraction],
            [FinancialRecords] = [FinancialCategories]
        };

    /// <summary>Processors that consume the completed output of <paramref name="processorKey"/>.</summary>
    public static IReadOnlyList<string> DependentsOf(string processorKey) =>
        Dependents.TryGetValue(processorKey, out string[]? dependents) ? dependents : [];
}
