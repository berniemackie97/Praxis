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
    /// The local tier's quick, low-trust triage of every readable source: category, document type,
    /// language, gist and expected record kinds. Chooses policy and schemas, never final facts.
    /// </summary>
    public const string IntakeTriage = "intake-triage-v1";

    /// <summary>
    /// The heavy understanding of every source: summary, knowledge and typed records from the strong
    /// tier, verified in code, corrected by the strongest tier when verification fails, and projected
    /// into typed datasets such as the financial ledger.
    /// </summary>
    public const string IntakeUnderstand = "intake-understand-v1";

    /// <summary>
    /// Fetches what a saved link points to (owner intent only, through the safe fetcher) and captures
    /// it as a new source, which then flows through the generic intake.
    /// </summary>
    public const string LinkFetch = "link-fetch-v1";

    /// <summary>The read-stage format a dependent requires before it is scheduled at all.</summary>
    private static readonly IReadOnlyDictionary<string, string> RequiredFormats =
        new Dictionary<string, string>(StringComparer.Ordinal) { [LinkFetch] = "link" };

    private static readonly IReadOnlyDictionary<string, string[]> Dependents =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [SourceText] = [TextSegments, IntakeTriage, LinkFetch],
            [TextSegments] = [SemanticExtraction],
            [IntakeTriage] = [IntakeUnderstand]
        };

    /// <summary>The document format a processor applies to, or null when it applies to every format.</summary>
    public static string? RequiredFormat(string processorKey) => RequiredFormats.GetValueOrDefault(processorKey);

    /// <summary>Processors that consume the completed output of <paramref name="processorKey"/>.</summary>
    public static IReadOnlyList<string> DependentsOf(string processorKey) =>
        Dependents.TryGetValue(processorKey, out string[]? dependents) ? dependents : [];
}
