using Praxis.Application.Documents;
using Praxis.Application.Documents.Email;
using Praxis.Application.Documents.Html;
using Praxis.Application.Documents.Ocr;
using Praxis.Application.Documents.Ooxml;
using Praxis.Application.Documents.Pdf;
using Praxis.Application.Documents.Tables;
using Praxis.Application.Enrichment;
using Praxis.Application.Finance;
using Praxis.Application.Links;
using Praxis.Application.Media;
using Praxis.Application.Policy;
using Praxis.Application.Processing;
using Praxis.Application.Text;
using Praxis.Application.Understanding;
using Praxis.Application.Understanding.Profiles;
using Praxis.Application.Verification;
using Praxis.Infrastructure.Documents;
using Praxis.Infrastructure.Inference;
using Praxis.Infrastructure.Inference.Ollama;
using Praxis.Infrastructure.Links;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Composition;

/// <summary>Composes the owner-local processors over an open catalog.</summary>
public static class CatalogProcessing
{
    /// <summary>
    /// The generic intake (ADR 0044): read every format, triage on the local tier, understand on the
    /// strong tier with code verification and strongest-tier correction, plus headings, segments and
    /// optional per-segment local enrichment. Disclosure policy governs every model call.
    /// </summary>
    public static IReadOnlyList<IProcessingHandler> CreateHandlers(
        SqliteCatalog catalog, IVerifierModelFactory? models = null, TimeProvider? clock = null, IOcrEngine? ocr = null,
        ITranscriptionEngine? transcriber = null, ILinkSnapshotWriter? linkSnapshots = null, ILinkFetcher? linkFetcher = null,
        IReadOnlyList<IVideoLinkResolver>? videoResolvers = null, LocalPace? pace = null)
    {
        LocalPace localPace = pace ?? new LocalPace();
        TimeProvider time = clock ?? TimeProvider.System;
        IntakeLadder ladder = Ladder(catalog, models ?? new VerificationModelFactory(), time);
        TieredModels tiers = Tiers(catalog, models ?? new VerificationModelFactory(), time);
        DocumentReaders readers = Readers(ocr ?? new VisionOcrEngine());
        ILinkFetcher fetcher = linkFetcher ?? SharedFetcher.Value;
        return
        [
            new TextExtractionHandler(catalog.Text, catalog.Originals, catalog.Processing, readers, transcriber ?? new SpeechTranscriptionEngine()),
            new TextSegmentationHandler(catalog.Text, catalog.Segments, catalog.Processing),
            new SemanticExtractionHandler(catalog.Models, catalog.Text, catalog.Segments, catalog.Proposals,
                catalog.Knowledge, catalog.Processing, new OllamaChatModelConnector()),
            new TriageHandler(catalog.Understanding, catalog.Text, tiers, catalog.Policy),
            new UnderstandHandler(catalog.Understanding, catalog.Text, catalog.Originals, readers,
                new DocumentUnderstander(ladder, RecordProfiles.Default, new LocalDraftPolicy(catalog.Settings, localPace), localPace), new TransactionProfile(ladder, catalog.Understanding),
                catalog.Policy, catalog.Processing, time),
            new LinkFetchHandler(catalog.Links, fetcher, linkSnapshots, videoResolvers ?? [new YouTubeLinkResolver(fetcher)], time)
        ];
    }

    /// <summary>Every reader, signature-first, plain text last.</summary>
    public static DocumentReaders Readers(IOcrEngine ocr) => new(
    [
        new PdfDocumentReader(new PdfPigTextReader(), ocr), new WordDocumentReader(), new SpreadsheetReader(), new ImageDocumentReader(ocr),
        new EmailDocumentReader(), new HtmlDocumentReader(), new FinancialInterchangeReader(), new DelimitedDocumentReader(), new LinkReader(),
        new WebVideoReader(), new PlainTextReader()
    ]);

    public static TieredModels Tiers(SqliteCatalog catalog, IVerifierModelFactory models, TimeProvider clock) =>
        new(catalog.Policy, catalog.Verification, models, new DisclosureGate(catalog.Policy, clock), clock);

    public static IntakeLadder Ladder(SqliteCatalog catalog, IVerifierModelFactory models, TimeProvider clock) =>
        new(Tiers(catalog, models, clock), catalog.Policy, clock);

    public static ProcessingPipeline CreatePipeline(
        SqliteCatalog catalog, TimeSpan? idlePoll = null, Action<string>? status = null, TimeProvider? clock = null,
        IVerifierModelFactory? models = null, IOcrEngine? ocr = null, ILinkSnapshotWriter? linkSnapshots = null) =>
        new(catalog.Processing, CreateHandlers(catalog, models, clock, ocr, linkSnapshots: linkSnapshots, pace: Pace), clock ?? TimeProvider.System, idlePoll, status);

    /// <summary>The worker's measured local pace, shared by its pipeline and the settings page.</summary>
    public static LocalPace Pace { get; } = new();

    /// <summary>One safe fetcher per process: its connection pool is shared by every link job.</summary>
    private static readonly Lazy<SafeHttpFetcher> SharedFetcher = new(() => new SafeHttpFetcher());
}
