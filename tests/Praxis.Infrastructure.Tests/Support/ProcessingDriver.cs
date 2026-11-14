using Praxis.Application.Inference;
using Praxis.Application.Processing;
using Praxis.Application.Text;
using Praxis.Domain.Models;
using Praxis.Domain.Processing;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Documents;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Tests.Support;

/// <summary>
/// Steps one handler's jobs deterministically, as the pipeline would, so a test can assert
/// each stage's effect in isolation without running the background loop.
/// </summary>
internal sealed class ProcessingDriver(SqliteCatalog catalog, IProcessingHandler handler)
{
    private bool reopenExhausted;

    internal static ProcessingDriver Text(SqliteCatalog catalog) =>
        new(catalog, new TextExtractionHandler(catalog.Text, catalog.Originals, catalog.Processing, CatalogProcessing.Readers(new VisionOcrEngine())));

    /// <summary>Runs read, triage and understanding to quiescence, interleaved as the pipeline would.</summary>
    internal static async Task RunIntakeAsync(SqliteCatalog catalog, IReadOnlyList<IProcessingHandler> handlers)
    {
        ProcessingDriver[] drivers = new[] { ProcessorKeys.SourceText, ProcessorKeys.IntakeTriage, ProcessorKeys.IntakeUnderstand }
            .Select(key => new ProcessingDriver(catalog, handlers.Single(handler => handler.ProcessorKey == key))).ToArray();
        for (int step = 0; step < 200; step++)
        {
            bool progressed = false;
            foreach (ProcessingDriver driver in drivers)
            {
                progressed |= await driver.ProcessNextAsync();
            }

            if (!progressed)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Runs segmentation and local enrichment with a scripted local model that suggests every
    /// Markdown heading as a topic: unconfirmed suggestions, as the local tier produces them.
    /// </summary>
    internal static async Task SuggestHeadingTopicsAsync(SqliteCatalog catalog)
    {
        if (catalog.Models.Get(ModelProfile.LocalEnrichment) is not { Enabled: true })
        {
            catalog.Models.Save(new ModelProfile(ModelProfile.LocalEnrichment, "ollama", "http://127.0.0.1:11434", "qwen:9b", 16_384, Enabled: true));
        }

        ScriptedChatModel model = new(request =>
        {
            string[] headings = ScriptedChatModel.Excerpt(request).Split('\n')
                .Where(line => line.StartsWith("# ", StringComparison.Ordinal)).Select(line => line[2..].Trim()).ToArray();
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                topics = headings.Select(heading => new { name = heading, quote = heading }),
                entities = Array.Empty<object>(),
                relations = Array.Empty<object>()
            });
        });
        ProcessingDriver[] drivers =
        [
            new(catalog, new TextSegmentationHandler(catalog.Text, catalog.Segments, catalog.Processing)),
            new(catalog, TestHandlers.SemanticExtraction(catalog, _ => new ModelConnection(model)))
        ];
        foreach (ProcessingDriver driver in drivers)
        {
            while (await driver.ProcessNextAsync())
            {
            }
        }
    }

    internal async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        ProcessingLease? lease = Claim();
        if (lease is null && !reopenExhausted)
        {
            if (handler.TryReopenWork())
            {
                lease = Claim();
            }
            else
            {
                reopenExhausted = true;
            }
        }

        if (lease is null)
        {
            return false;
        }

        await handler.ProcessAsync(lease, cancellationToken);
        return true;
    }

    /// <summary>Synchronous step for handlers that complete without awaiting I/O.</summary>
    internal bool ProcessNext() => ProcessNextAsync().GetAwaiter().GetResult();

    private ProcessingLease? Claim() =>
        catalog.Processing.TryClaim(handler.LeaseDuration, processorKey: handler.ProcessorKey);
}
