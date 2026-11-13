using Praxis.Application.Enrichment;
using Praxis.Application.Finance;
using Praxis.Application.Policy;
using Praxis.Application.Processing;
using Praxis.Application.Text;
using Praxis.Application.Verification;
using Praxis.Infrastructure.Documents;
using Praxis.Infrastructure.Inference;
using Praxis.Infrastructure.Inference.Ollama;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Composition;

/// <summary>Composes the owner-local processors over an open catalog.</summary>
public static class CatalogProcessing
{
    /// <summary>
    /// Extraction, heading proposals, segmentation, local-model enrichment (deferred while no model
    /// is enabled) and model-tiered financial intake governed by the disclosure policy (ADR 0043).
    /// </summary>
    public static IReadOnlyList<IProcessingHandler> CreateHandlers(
        SqliteCatalog catalog, IVerifierModelFactory? models = null, TimeProvider? clock = null)
    {
        TimeProvider time = clock ?? TimeProvider.System;
        IntakeLadder ladder = Ladder(catalog, models ?? new VerificationModelFactory(), time);
        return
        [
            new TextExtractionHandler(catalog.Text, catalog.Originals, catalog.Processing),
            new HeadingProposalHandler(catalog.Text, catalog.Proposals, catalog.Processing),
            new TextSegmentationHandler(catalog.Text, catalog.Segments, catalog.Processing),
            new SemanticExtractionHandler(catalog.Models, catalog.Text, catalog.Segments, catalog.Proposals,
                catalog.Knowledge, catalog.Processing, new OllamaChatModelConnector()),
            new FinancialRecordsHandler(catalog.Finance, catalog.Originals, catalog.Processing, new PdfPigTextReader(), ladder, catalog.Policy, time),
            new FinancialAnnotationHandler(catalog.Finance, ladder, catalog.Processing)
        ];
    }

    public static IntakeLadder Ladder(SqliteCatalog catalog, IVerifierModelFactory models, TimeProvider clock) =>
        new(new TieredModels(catalog.Policy, catalog.Verification, models, new DisclosureGate(catalog.Policy, clock), clock), catalog.Policy, clock);

    public static ProcessingPipeline CreatePipeline(
        SqliteCatalog catalog, TimeSpan? idlePoll = null, Action<string>? status = null, TimeProvider? clock = null,
        IVerifierModelFactory? models = null) =>
        new(catalog.Processing, CreateHandlers(catalog, models, clock), clock ?? TimeProvider.System, idlePoll, status);
}
