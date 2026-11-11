using Praxis.Application.Desktop;
using Praxis.Application.Desktop.Actions;
using Praxis.Application.Intake;
using Praxis.Application.Media;
using Praxis.Application.Processing;
using Praxis.Application.Verification;
using Praxis.Infrastructure.Inference.Ollama;
using Praxis.Infrastructure.Inference.Subscription;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Tests.Support;

/// <summary>The desktop protocol over a test catalog, with optional intake and background services.</summary>
internal static class TestDesktop
{
    internal static DesktopProtocol Protocol(
        SqliteCatalog catalog, QuickNoteCapture? quickCapture = null, WatchedFolderIntake? monitor = null,
        ProcessingPipeline? processing = null, VerificationRunner? verification = null, WatchedFolderIntakeGroup? group = null)
    {
        IIntakeSupervisor? intake = group is not null ? group : monitor is null ? null : new SingleFolderIntakeSupervisor(monitor);
        SubscriptionVerifierModelFactory verifierModels = new();
        return new DesktopProtocol(
        [
            new LibraryActions(catalog.Library, catalog.Text, catalog.Sources, catalog.Processing, processing),
            new OriginalActions(catalog.Originals),
            new CollectionActions(catalog.Collections),
            new SearchActions(catalog.Search, catalog.Embeddings, catalog.Models, new OllamaEmbeddingModelConnector()),
            new MediaActions(new MediaEvidence(catalog.Sources, catalog.Search, catalog.Text, catalog.Originals)),
            new CaptureActions(quickCapture, intake, processing),
            new EnrichmentActions(catalog.Proposals),
            new IntakeActions(intake, catalog.Processing, catalog.IntakeIncidents),
            new KnowledgeActions(catalog.Knowledge),
            new VerificationActions(catalog.Verification, new VerificationApproval(catalog.Verification),
                new VerificationProviderConfiguration(catalog.Verification, verifierModels), verifierModels, verification),
            new ModelActions(catalog.Models, new OllamaModelDirectory(), processing)
        ]);
    }
}
