using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Praxis.Application.Collections;
using Praxis.Application.Desktop;
using Praxis.Application.Desktop.Actions;
using Praxis.Application.Enrichment;
using Praxis.Application.Inference;
using Praxis.Application.Intake;
using Praxis.Application.Knowledge;
using Praxis.Application.Library;
using Praxis.Application.Media;
using Praxis.Application.Models;
using Praxis.Application.Originals;
using Praxis.Application.Processing;
using Praxis.Application.Search;
using Praxis.Application.Sources;
using Praxis.Application.Text;
using Praxis.Application.Verification;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Inference;
using Praxis.Infrastructure.Inference.Ollama;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Persistence;
using Praxis.Worker.Desktop;
using Praxis.Worker.Hosting;

namespace Praxis.Worker.Composition;

/// <summary>The worker's composition root: catalog, ports, use cases, intake, desktop protocol and supervision.</summary>
internal static class WorkerServiceRegistration
{
    internal static IServiceCollection AddPraxisWorker(this IServiceCollection services, WorkerOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<WorkerOutcome>();

        services.AddSingleton(provider => SqliteCatalog.Open(options.DataRoot, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ISourceRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Sources);
        services.AddSingleton<IOriginalStore>(provider => provider.GetRequiredService<SqliteCatalog>().Originals);
        services.AddSingleton<ILibraryReader>(provider => provider.GetRequiredService<SqliteCatalog>().Library);
        services.AddSingleton<IProcessingQueue>(provider => provider.GetRequiredService<SqliteCatalog>().Processing);
        services.AddSingleton<IIntakeIncidentJournal>(provider => provider.GetRequiredService<SqliteCatalog>().IntakeIncidents);
        services.AddSingleton<ITextDerivativeRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Text);
        services.AddSingleton<ITextSegmentRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Segments);
        services.AddSingleton<ITextSearchIndex>(provider => provider.GetRequiredService<SqliteCatalog>().Search);
        services.AddSingleton<ISemanticVectorStore>(provider => provider.GetRequiredService<SqliteCatalog>().Embeddings);
        services.AddSingleton<IProposalRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Proposals);
        services.AddSingleton<IKnowledgeRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Knowledge);
        services.AddSingleton<IVerificationRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Verification);
        services.AddSingleton<ICollectionRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Collections);
        services.AddSingleton<IModelProfileRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Models);

        services.AddSingleton<IVerifierModelFactory, VerificationModelFactory>();
        services.AddSingleton<IChatModelConnector, OllamaChatModelConnector>();
        services.AddSingleton<IEmbeddingModelConnector, OllamaEmbeddingModelConnector>();
        services.AddSingleton<ILocalModelDirectory, OllamaModelDirectory>();

        services.AddSingleton(provider => CatalogProcessing.CreatePipeline(
            provider.GetRequiredService<SqliteCatalog>(),
            status: Report(provider, "Praxis.Processing"),
            clock: provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<VerificationApproval>();
        services.AddSingleton<VerificationProviderConfiguration>();
        services.AddSingleton(provider => new VerificationRunner(
            provider.GetRequiredService<IVerificationRepository>(), provider.GetRequiredService<IVerifierModelFactory>(),
            provider.GetRequiredService<TimeProvider>(), status: Report(provider, "Praxis.Verification")));
        services.AddSingleton<MediaEvidence>();

        services.AddSingleton(provider => new WatchedFolderIntakeGroup(
            provider.GetRequiredService<ISourceRepository>(), provider.GetRequiredService<IOriginalStore>(), options.DataRoot,
            options.IntakeRoots, status: Report(provider, "Praxis.Intake"),
            onCaptured: provider.GetRequiredService<ProcessingPipeline>().Wake,
            clock: provider.GetRequiredService<TimeProvider>(),
            incidents: provider.GetRequiredService<IIntakeIncidentJournal>()));
        services.AddSingleton<IIntakeSupervisor>(provider => provider.GetRequiredService<WatchedFolderIntakeGroup>());
        services.AddSingleton<IQuickNoteCapture>(provider => new QuickNoteCapture(
            provider.GetRequiredService<IOriginalStore>(), options.DataRoot, options.PrimaryIntakeRoot));

        services.AddSingleton<IDesktopActionGroup, LibraryActions>();
        services.AddSingleton<IDesktopActionGroup, OriginalActions>();
        services.AddSingleton<IDesktopActionGroup, CollectionActions>();
        services.AddSingleton<IDesktopActionGroup, SearchActions>();
        services.AddSingleton<IDesktopActionGroup, MediaActions>();
        services.AddSingleton<IDesktopActionGroup, CaptureActions>();
        services.AddSingleton<IDesktopActionGroup, EnrichmentActions>();
        services.AddSingleton<IDesktopActionGroup, IntakeActions>();
        services.AddSingleton<IDesktopActionGroup, KnowledgeActions>();
        services.AddSingleton<IDesktopActionGroup, VerificationActions>();
        services.AddSingleton<IDesktopActionGroup, ModelActions>();
        services.AddSingleton<DesktopProtocol>();
        services.AddSingleton<DesktopStdioSession>();

        services.AddHostedService<WorkerSupervisor>();
        return services;
    }

    /// <summary>Routes a component's fixed operational status messages to a named log category.</summary>
    private static Action<string> Report(IServiceProvider provider, string category)
    {
        ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(category);
        return message => logger.LogInformation("{Status}", message);
    }
}
