using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Praxis.Application.Activity;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Capabilities;
using Praxis.Application.Agents.Memory;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Agents.Skills;
using Praxis.Application.Collections;
using Praxis.Application.Desktop;
using Praxis.Application.Desktop.Actions;
using Praxis.Application.Enrichment;
using Praxis.Application.Finance;
using Praxis.Application.Inference;
using Praxis.Application.Intake;
using Praxis.Application.Knowledge;
using Praxis.Application.Library;
using Praxis.Application.Links;
using Praxis.Application.Media;
using Praxis.Application.Models;
using Praxis.Application.Originals;
using Praxis.Application.Policy;
using Praxis.Application.Processing;
using Praxis.Application.Projects;
using Praxis.Application.Search;
using Praxis.Application.Sources;
using Praxis.Application.Text;
using Praxis.Application.Understanding;
using Praxis.Application.Verification;
using Praxis.Infrastructure.Agents;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Inference;
using Praxis.Infrastructure.Inference.Ollama;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Links;
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
        services.AddSingleton<IGraphReader>(provider => provider.GetRequiredService<SqliteCatalog>().Graph);
        services.AddSingleton<IKnowledgeReconsolidation>(provider => provider.GetRequiredService<SqliteCatalog>().Reconsolidation);
        services.AddSingleton<RevisionReview>();
        services.AddSingleton(provider => new ReconsolidationRunner(provider.GetRequiredService<IKnowledgeReconsolidation>(),
            provider.GetRequiredService<TimeProvider>(), status: Report(provider, "Praxis.Reconsolidation")));
        services.AddSingleton<IVerificationRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Verification);
        services.AddSingleton<ICollectionRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Collections);
        services.AddSingleton<IModelProfileRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Models);
        services.AddSingleton<IFinancialLedgerRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Finance);
        services.AddSingleton<IUnderstandingRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Understanding);
        services.AddSingleton<IPolicyRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Policy);
        services.AddSingleton<PolicyService>();

        services.AddSingleton<IVerifierModelFactory, VerificationModelFactory>();
        services.AddSingleton<IChatModelConnector, OllamaChatModelConnector>();
        services.AddSingleton<IEmbeddingModelConnector, OllamaEmbeddingModelConnector>();
        services.AddSingleton<ILocalModelDirectory, OllamaModelDirectory>();

        services.AddSingleton(provider => CatalogProcessing.CreatePipeline(
            provider.GetRequiredService<SqliteCatalog>(),
            status: Report(provider, "Praxis.Processing"),
            clock: provider.GetRequiredService<TimeProvider>(),
            models: provider.GetRequiredService<IVerifierModelFactory>(),
            linkSnapshots: new LinkSnapshotWriter(provider.GetRequiredService<IOriginalStore>(), options.DataRoot, options.PrimaryIntakeRoot, RequirePrimaryInbox(provider))));
        services.AddSingleton<VerificationApproval>();
        services.AddSingleton<VerificationProviderConfiguration>();
        services.AddSingleton<IModelToolDetector, ModelToolDetector>();
        services.AddSingleton<ISettingsStore>(provider => provider.GetRequiredService<SqliteCatalog>().Settings);
        services.AddSingleton(_ => CatalogProcessing.Pace);
        services.AddSingleton(_ => CatalogProcessing.Activity);
        services.AddSingleton<IActivityReader>(provider => provider.GetRequiredService<SqliteCatalog>().Activity);
        services.AddSingleton<ModelSetup>();
        services.AddSingleton(provider => new VerificationRunner(
            provider.GetRequiredService<IVerificationRepository>(), provider.GetRequiredService<IVerifierModelFactory>(),
            provider.GetRequiredService<TimeProvider>(), status: Report(provider, "Praxis.Verification"),
            mayStart: () => !provider.GetRequiredService<IAgentRunJournal>().HasActiveDream()));
        services.AddSingleton<MediaEvidence>();
        services.AddSingleton(provider => new MediaPlayback(provider.GetRequiredService<ISourceRepository>(),
            provider.GetRequiredService<IOriginalStore>(), provider.GetRequiredService<MediaEvidence>(),
            new WebVideoPlayback(provider.GetRequiredService<ISourceRepository>(), provider.GetRequiredService<IOriginalStore>())));
        services.AddSingleton<FinancialLedger>();
        services.AddSingleton<IChatMemoryStore>(provider => provider.GetRequiredService<SqliteCatalog>().ChatMemory);
        services.AddSingleton<IChatMemoryNotes>(provider => new ChatMemoryNotes(
            provider.GetRequiredService<ISourceRepository>(), provider.GetRequiredService<ITextSearchIndex>(),
            provider.GetRequiredService<IOriginalStore>(), provider.GetRequiredService<IPolicyRepository>(),
            provider.GetRequiredService<IIntakeSupervisor>(), options.DataRoot, options.PrimaryIntakeRoot));
        services.AddSingleton<ChatMemoryMaintenance>();
        services.AddSingleton<AgentLearningReviewer>();
        services.AddSingleton<IAgentRecallJournal>(provider => provider.GetRequiredService<SqliteCatalog>().AgentRecalls);
        services.AddSingleton<IAgentRunJournal>(provider => provider.GetRequiredService<SqliteCatalog>().AgentRuns);
        services.AddSingleton<IProcessingControls>(provider => provider.GetRequiredService<SqliteCatalog>().ProcessingControls);
        services.AddSingleton<IAgentConversationRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Agents);
        services.AddSingleton(_ => new SessionSecretStore(new KeychainSecretStore()));
        services.AddSingleton<ISecretStore>(provider => provider.GetRequiredService<SessionSecretStore>());
        services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) });
        services.AddSingleton<ISavedLinkWriter>(provider => new SavedLinkWriter(provider.GetRequiredService<IOriginalStore>(), options.DataRoot, options.PrimaryIntakeRoot, RequirePrimaryInbox(provider)));
        services.AddSingleton<ILinkRepository>(provider => provider.GetRequiredService<SqliteCatalog>().Links);
        services.AddSingleton<IArtifactWriter>(provider => new ArtifactWriter(provider.GetRequiredService<IOriginalStore>(), options.DataRoot, options.PrimaryIntakeRoot, RequirePrimaryInbox(provider)));
        services.AddSingleton<IAgentBackendFactory>(provider => new AgentBackendFactory(provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<ISecretStore>(), RelayCommand(), Path.GetTempPath()));
        services.AddSingleton(provider => AgentComposition.Capabilities(provider.GetRequiredService<SqliteCatalog>()));
        services.AddSingleton(provider => AgentComposition.Skills(provider.GetRequiredService<SqliteCatalog>()));
        services.AddSingleton(provider => AgentComposition.Runtime(provider.GetRequiredService<SqliteCatalog>(),
            AgentComposition.Tools(provider.GetRequiredService<SqliteCatalog>(), provider.GetRequiredService<IEmbeddingModelConnector>(),
                provider.GetRequiredService<IArtifactWriter>(), provider.GetRequiredService<IVerifierModelFactory>(), provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ISavedLinkWriter>(), provider.GetRequiredService<IChatMemoryNotes>(),
                provider.GetRequiredService<SkillLibrary>(), provider.GetRequiredService<CapabilityService>()),
            provider.GetRequiredService<IAgentBackendFactory>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<SkillLibrary>()));
        services.AddSingleton<AgentRunCoordinator>();
        services.AddSingleton(provider => new ProposalApplier(provider.GetRequiredService<IKnowledgeRepository>(), provider.GetRequiredService<IPolicyRepository>(), provider.GetRequiredService<IAgentConversationRepository>()));

        services.AddSingleton(provider => new WatchedFolderIntakeGroup(
            provider.GetRequiredService<ISourceRepository>(), provider.GetRequiredService<IOriginalStore>(), options.DataRoot,
            options.IntakeRoots, status: Report(provider, "Praxis.Intake"),
            onCaptured: provider.GetRequiredService<ProcessingPipeline>().Wake,
            clock: provider.GetRequiredService<TimeProvider>(),
            incidents: provider.GetRequiredService<IIntakeIncidentJournal>(), pendingRoots: options.PendingIntakeRoots));
        services.AddSingleton<IIntakeSupervisor>(provider => provider.GetRequiredService<WatchedFolderIntakeGroup>());
        services.AddSingleton<IQuickNoteCapture>(provider => new QuickNoteCapture(
            provider.GetRequiredService<IOriginalStore>(), options.DataRoot, options.PrimaryIntakeRoot));

        services.AddSingleton<IDesktopActionGroup, LibraryActions>();
        services.AddSingleton<IDesktopActionGroup, OriginalActions>();
        services.AddSingleton<IDesktopActionGroup, CollectionActions>();
        services.AddSingleton<IDesktopActionGroup, SearchActions>();
        services.AddSingleton<IDesktopActionGroup, MediaActions>();
        services.AddSingleton<IDesktopActionGroup, LinkActions>();
        services.AddSingleton<IDesktopActionGroup, CaptureActions>();
        services.AddSingleton<IDesktopActionGroup, EnrichmentActions>();
        services.AddSingleton<IDesktopActionGroup, IntakeActions>();
        services.AddSingleton<IDesktopActionGroup, KnowledgeActions>();
        services.AddSingleton<IDesktopActionGroup, ReconsolidationActions>();
        services.AddSingleton<IDesktopActionGroup, GraphActions>();
        services.AddSingleton<IDesktopActionGroup, VerificationActions>();
        services.AddSingleton<IDesktopActionGroup, ModelSetupActions>();
        services.AddSingleton<IDesktopActionGroup, ModelActions>();
        services.AddSingleton<IDesktopActionGroup, FinanceActions>();
        services.AddSingleton<IDesktopActionGroup, UnderstandingActions>();
        services.AddSingleton<IDesktopActionGroup, PolicyActions>();
        services.AddSingleton<IDesktopActionGroup, AgentActions>();
        services.AddSingleton<IDesktopActionGroup, SkillActions>();
        services.AddSingleton<IDesktopActionGroup, CapabilityActions>();
        services.AddSingleton<IDesktopActionGroup, ActivityActions>();
        services.AddSingleton<IDesktopActionGroup, BrainWorkActions>();
        services.AddSingleton<DesktopProtocol>();
        services.AddSingleton<DesktopStdioSession>();

        services.AddHostedService<AgentMaintenanceService>();
        services.AddHostedService<WorkerSupervisor>();
        services.AddHostedService<ChatMemoryService>();
        services.AddSingleton<SemanticIndexMaintenance>();
        services.AddHostedService<SemanticIndexService>();
        services.AddSingleton(provider => new ProjectUnderstander(provider.GetRequiredService<SqliteCatalog>().Projects,
            provider.GetRequiredService<SqliteCatalog>().Text,
            CatalogProcessing.Ladder(provider.GetRequiredService<SqliteCatalog>(), provider.GetRequiredService<IVerifierModelFactory>(),
                provider.GetRequiredService<TimeProvider>()),
            provider.GetRequiredService<IPolicyRepository>(), provider.GetRequiredService<TimeProvider>(),
            sources => GitProjectRoots.Find(sources, provider.GetRequiredService<WatchedFolderIntakeGroup>().AvailableRoots())));
        services.AddHostedService<ProjectUnderstandingService>();
        return services;
    }

    // Resolve at the point of writing: the intake supervisor itself wakes the processing pipeline.
    private static Action RequirePrimaryInbox(IServiceProvider provider) =>
        () => provider.GetRequiredService<IIntakeSupervisor>().EnsurePrimaryRootApproved();

    /// <summary>How CLI agents launch this worker as their Praxis MCP relay (apphost, or dotnet plus the worker assembly).</summary>
    private static AgentRelayCommand RelayCommand()
    {
        string process = Environment.ProcessPath ?? throw new InvalidOperationException("The worker path is unknown.");
        string assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
        return Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new AgentRelayCommand(process, [assembly])
            : new AgentRelayCommand(process, []);
    }

    /// <summary>Routes a component's fixed operational status messages to a named log category.</summary>
    private static Action<string> Report(IServiceProvider provider, string category)
    {
        ILogger logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(category);
        return message => logger.LogInformation("{Status}", message);
    }
}
