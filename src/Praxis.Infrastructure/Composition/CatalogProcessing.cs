using Praxis.Application.Enrichment;
using Praxis.Application.Processing;
using Praxis.Application.Text;
using Praxis.Infrastructure.Inference.Ollama;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Composition;

/// <summary>Composes the owner-local processors over an open catalog.</summary>
public static class CatalogProcessing
{
    /// <summary>
    /// Extraction, heading proposals, segmentation and model enrichment with the owner's
    /// configured local model (deferred while none is enabled).
    /// </summary>
    public static IReadOnlyList<IProcessingHandler> CreateHandlers(SqliteCatalog catalog) =>
    [
        new TextExtractionHandler(catalog.Text, catalog.Originals, catalog.Processing),
        new HeadingProposalHandler(catalog.Text, catalog.Proposals, catalog.Processing),
        new TextSegmentationHandler(catalog.Text, catalog.Segments, catalog.Processing),
        new SemanticExtractionHandler(catalog.Models, catalog.Text, catalog.Segments, catalog.Proposals,
            catalog.Knowledge, catalog.Processing, new OllamaChatModelConnector())
    ];

    public static ProcessingPipeline CreatePipeline(
        SqliteCatalog catalog, TimeSpan? idlePoll = null, Action<string>? status = null, TimeProvider? clock = null) =>
        new(catalog.Processing, CreateHandlers(catalog), clock ?? TimeProvider.System, idlePoll, status);
}
