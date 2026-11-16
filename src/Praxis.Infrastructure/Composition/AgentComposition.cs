using Praxis.Application.Agents;
using Praxis.Application.Agents.Capabilities;
using Praxis.Application.Agents.Data;
using Praxis.Application.Agents.Memory;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Agents.Skills;
using Praxis.Application.Agents.Tools;
using Praxis.Application.Finance;
using Praxis.Application.Inference;
using Praxis.Application.Links;
using Praxis.Application.Media;
using Praxis.Application.Policy;
using Praxis.Application.Search;
using Praxis.Application.Verification;
using Praxis.Infrastructure.Agents;
using Praxis.Infrastructure.Agents.Capabilities;
using Praxis.Infrastructure.Documents;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Composition;

/// <summary>Composes the agent tool surface, registry and runtime over an open catalog.</summary>
public static class AgentComposition
{
    public static SkillLibrary Skills(SqliteCatalog catalog) => new(BundledSkills.Documents, catalog.Skills, new YamlSkillDocumentParser());

    public static CapabilityService Capabilities(SqliteCatalog catalog) => new(catalog.Capabilities, new LocalCapabilityExecutor());

    public static IReadOnlyList<IAgentTool> Tools(
        SqliteCatalog catalog, IEmbeddingModelConnector embeddings, IArtifactWriter artifacts, IVerifierModelFactory models, TimeProvider clock,
        ISavedLinkWriter? links = null, IChatMemoryNotes? memoryNotes = null, SkillLibrary? skills = null,
        CapabilityService? capabilities = null)
    {
        FinancialLedger ledger = new(catalog.Finance);
        PolicyService policy = new(catalog.Policy);
        IDataset[] datasets = [new TransactionsDataset(ledger), new AccountsDataset(ledger), new FinancialDocumentsDataset(ledger), new RecordsDataset(catalog.Understanding)];
        TieredModels tiers = new(catalog.Policy, catalog.Verification, models, new DisclosureGate(catalog.Policy, clock), clock, CatalogProcessing.Activity);
        PdfPigTextReader pdf = new();
        MediaMoments moments = new(catalog.Text, new MediaEvidence(catalog.Sources, catalog.Search, catalog.Text, catalog.Originals, catalog.Collections));
        KnowledgeRetriever retriever = new(catalog.Search, catalog.Embeddings, catalog.Models, embeddings);
        SkillLibrary library = skills ?? Skills(catalog);
        return
        [
            new SkillSearchTool(library),
            new SkillOpenTool(library),
            new SkillProposeTool(library),
            new KnowledgeSearchTool(catalog.Search, catalog.Embeddings, catalog.Models, embeddings, catalog.Knowledge, catalog.Policy),
            new KnowledgeOpenTool(catalog.Text, catalog.Sources, catalog.Originals, pdf, moments),
            new MediaFindTool(retriever, moments, catalog.Sources),
            new KnowledgeEntityTool(catalog.Knowledge),
            new KnowledgeRelatedTool(catalog.Knowledge),
            new KnowledgeProjectTool(catalog.Projects),
            new KnowledgeTimelineTool(catalog.Library, catalog.Sources, ledger, catalog.Understanding),
            new KnowledgeProposeTool(catalog.Agents, new ProposalApplier(catalog.Knowledge, catalog.Policy)),
            new DataDescribeTool(datasets),
            new DataQueryTool(datasets),
            new ComputeEvaluateTool(),
            new ArtifactCreateTool(artifacts, catalog.Agents, clock),
            new ArtifactUpdateTool(artifacts, catalog.Agents, clock),
            new MemoryRecallTool(catalog.Agents),
            new MemoryRememberTool(catalog.Agents),
            .. (memoryNotes is null ? Array.Empty<IAgentTool>() : [new MemoryEditTool(memoryNotes, catalog.ChatMemory, catalog.Agents)]),
            new IntakeStatusTool(catalog.Processing, catalog.Policy, ledger),
            .. (links is null ? Array.Empty<IAgentTool>() : [new KnowledgeSaveLinkTool(links, catalog.Agents, catalog.Links)]),
            new IntakeReprocessTool(catalog.Processing),
            new SettingsReadTool(policy, catalog.Policy),
            new SettingsProposeTool(policy, clock),
            new AgentDelegateTool(tiers, catalog.Text, catalog.Sources),
            .. SystemActionTools.All,
            .. (capabilities is null ? Array.Empty<IAgentTool>() : [new CapabilityRequestTool(capabilities), new CapabilityResultTool(capabilities), new ProgramDiscoveryTool(new InstalledProgramProbe())])
        ];
    }

    public static AgentRuntime Runtime(SqliteCatalog catalog, IReadOnlyList<IAgentTool> tools, IAgentBackendFactory backends, TimeProvider clock, SkillLibrary? skills = null) =>
        new(catalog.Agents, new AgentToolRegistry(tools, catalog.Agents, clock), backends, new PolicyService(catalog.Policy),
            new DisclosureGate(catalog.Policy, clock), clock, catalog.Search, catalog.ChatMemory, skills ?? Skills(catalog), catalog.AgentRecalls);
}
