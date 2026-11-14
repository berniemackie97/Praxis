using System.Text;
using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Data;
using Praxis.Application.Agents.Tools;
using Praxis.Application.Finance;
using Praxis.Application.Inference;
using Praxis.Application.Knowledge;
using Praxis.Application.Policy;
using Praxis.Application.Understanding;
using Praxis.Domain.Models;
using Praxis.Domain.Originals;
using Praxis.Domain.Policy;
using Praxis.Domain.Processing;
using Praxis.Domain.Understanding;
using Praxis.Domain.Verification;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Agents;
using Praxis.Infrastructure.Tests.Documents;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Understanding;

/// <summary>Any readable source goes through the same cascade: read, local triage, strong understanding, code verification, strongest on failure.</summary>
public sealed class IntakeEngineTests : IDisposable
{
    private const string Local = VerificationProvider.Ollama;
    private const string Strong = VerificationProvider.ClaudeCode;
    private const string Strongest = VerificationProvider.Codex;

    private const string Notes = """
        # Kitchen renovation
        Ortega Renovations will install the cabinets. Alex Ortega leads the crew.
        Site visit on Nov 20, 2026 at 2:30 pm.
        """;

    private const string Triage = """{"category":"documents","documentType":"meeting notes","language":"en","gist":"Kitchen renovation notes.","recordKinds":["event"]}""";

    private const string GoodUnderstanding = """
        {"summary":"Notes on the kitchen renovation and the site visit.","documentType":"meeting notes",
         "topics":[{"name":"Kitchen renovation","quote":"Kitchen renovation"}],
         "entities":[{"name":"Ortega Renovations","type":"organization","quote":"Ortega Renovations will install the cabinets."},
                     {"name":"Alex Ortega","type":"person","quote":"Alex Ortega leads the crew."}],
         "relations":[],
         "records":[{"kind":"event","title":"Site visit","start":"2026-11-20T14:30","quote":"Site visit on Nov 20, 2026 at 2:30 pm."}]}
        """;

    private const string InventedUnderstanding = """
        {"summary":"Notes.","documentType":"notes","topics":[],"relations":[],"records":[],
         "entities":[{"name":"Acme","type":"organization","quote":"Acme will pay"},{"name":"Zed","type":"person","quote":"Zed called"}]}
        """;

    private readonly string workspace = Path.Combine(TestPaths.Temp, "praxis-engine-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));

    public IntakeEngineTests()
    {
        Directory.CreateDirectory(Path.Combine(workspace, "store"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(workspace, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(workspace, "store"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task AnyDocumentIsTriagedLocallyUnderstoodStronglyAndVerifiedIntoKnowledgeAndRecords()
    {
        using SqliteCatalog catalog = Open();
        Grant(catalog, DataCategories.Documents, Strong);
        ScriptedModelFactory models = Models(_ => GoodUnderstanding);
        CapturedOriginal note = await Capture(catalog, "renovation.docx", OfficeFixtures.Docx("Renovation", Notes.Split('\n')));
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));

        // The local tier triages and drafts; with nothing usable in its draft the strong tier reads the file itself.
        Assert.Equal(2, models.Requests[Local].Count);
        Assert.Single(models.Requests[Strong]);
        Assert.Equal(0, models.Count(Strongest));
        SourceUnderstanding understood = catalog.Understanding.ReadCurrent(note.SourceId)!;
        Assert.Equal((UnderstandingStatuses.Understood, "docx", ModelTiers.Strong), (understood.Status, understood.Format, understood.Tier));
        Assert.Equal(("meeting notes", DataCategories.Documents, 1), (understood.DocumentType, understood.Category, understood.Records));
        Assert.Equal("Notes on the kitchen renovation and the site visit.", understood.Summary);
        Assert.Contains(catalog.Knowledge.ListEntities(null, "Ortega Renovations", 10), entity => entity.EntityType == "organization");
        Assert.Contains(catalog.Policy.ReadInvocations(10), call => call.ProviderId == Local && !call.Remote);

        // The agent can query the verified event and see it on the timeline, cited to its quote.
        RecordsDataset records = new(catalog.Understanding);
        DataRow row = Assert.Single(records.Snapshot(AgentPrimitivesTests.Context()).Rows);
        Assert.Equal(("event", "Site visit", new DateOnly(2026, 11, 20), "14:30"), ((string)row.Values["kind"]!, (string)row.Values["title"]!,
            (DateOnly)row.Values["date"]!, (string)row.Values["time"]!));
        Assert.StartsWith("chars:", Assert.Single(row.Evidence).Locator, StringComparison.Ordinal);
        Assert.Empty(records.Snapshot(AgentPrimitivesTests.Context(mayDisclose: _ => false)).Rows);
        AgentToolOutput timeline = await new KnowledgeTimelineTool(catalog.Library, catalog.Sources, new FinancialLedger(catalog.Finance), catalog.Understanding)
            .InvokeAsync(JsonDocument.Parse("""{"from":"2026-11-16","to":"2026-11-22"}""").RootElement, AgentPrimitivesTests.Context(), CancellationToken.None);
        Assert.Contains("Site visit", timeline.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLocalDraftIsReviewedByTheStrongTierWhichWritesOnlyCorrections()
    {
        using SqliteCatalog catalog = Open();
        Grant(catalog, DataCategories.Documents, Strong);
        const string localDraft = """
            {"summary":"Kitchen renovation notes.","documentType":"meeting notes","topics":[],"relations":[],
             "entities":[{"name":"Ortega Renovations","type":"organization","quote":"Ortega Renovations will install the cabinets."},
                         {"name":"Alex Ortega","type":"person","quote":"Alex Ortega leads the crew."},
                         {"name":"Site visit","type":"concept","quote":"Site visit on Nov 20, 2026 at 2:30 pm."}],
             "records":[{"kind":"event","title":"Site visit","start":"2026-11-20T14:30","quote":"Site visit on Nov 20, 2026 at 2:30 pm."}]}
            """;
        const string corrections = """
            {"remove":["E3"],"add":{"topics":[{"name":"Kitchen renovation","quote":"Kitchen renovation"}],"entities":[],"relations":[],"records":[]},
             "summary":"","documentType":""}
            """;
        ScriptedModelFactory models = new ScriptedModelFactory()
            .Answer(Local, request => TransactionIs(request, "recordKinds") ? Triage : localDraft)
            .Answer(Strong, request => TransactionIs(request, "remove") ? corrections : "{}");
        CapturedOriginal note = await Capture(catalog, "renovation.md", Encoding.UTF8.GetBytes(Notes));
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));

        // One strong call, reviewing the draft rather than reading from scratch.
        ChatRequest review = Assert.Single(models.Requests[Strong]);
        Assert.True(TransactionIs(review, "remove"));
        Assert.Contains("<draft>", review.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Equal(0, models.Count(Strongest));

        SourceUnderstanding understood = catalog.Understanding.ReadCurrent(note.SourceId)!;
        Assert.Equal((UnderstandingStatuses.Understood, ModelTiers.Strong), (understood.Status, understood.Tier));
        Assert.Equal("Kitchen renovation notes.", understood.Summary);
        Assert.StartsWith("local drafted 4 → strong kept 3, removed 1, added 1 → 4 verified", understood.Cascade, StringComparison.Ordinal);
        Assert.Contains(catalog.Knowledge.ListEntities(null, "Ortega Renovations", 10), entity => entity.EntityType == "organization");
        Assert.Empty(catalog.Knowledge.ListEntities(null, "Site visit", 10));
        Assert.Equal(1, understood.Records);
    }

    [Fact]
    public async Task FilesLeftIndexOnlyForLackOfAModelAreUnderstoodOnceOneIsConnected()
    {
        using SqliteCatalog catalog = Open();
        catalog.Policy.SaveTier(ModelTiers.Strong, null);
        catalog.Policy.SaveTier(ModelTiers.Strongest, null);
        ScriptedModelFactory models = Models(_ => GoodUnderstanding);
        CapturedOriginal note = await Capture(catalog, "renovation.md", Encoding.UTF8.GetBytes(Notes));
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));
        Assert.Equal(UnderstandingStatuses.IndexOnly, catalog.Understanding.ReadCurrent(note.SourceId)!.Status);

        // Nothing changes on its own: a failed file is not retried in a loop.
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));
        Assert.Empty(models.Requests.GetValueOrDefault(Strong) ?? []);

        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        catalog.Policy.SaveTier(ModelTiers.Strong, Strong);
        Grant(catalog, DataCategories.Documents, Strong);
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));

        Assert.Equal(UnderstandingStatuses.Understood, catalog.Understanding.ReadCurrent(note.SourceId)!.Status);
    }

    [Fact]
    public async Task FilesBlockedByADailyLimitAreReadAgainTheNextDay()
    {
        using SqliteCatalog catalog = Open();
        catalog.Verification.SaveProvider(new VerificationProvider(Strong, "/usr/bin/true", "sonnet", 10, 1, true));
        catalog.Policy.SaveTier(ModelTiers.Strongest, null);
        Grant(catalog, DataCategories.Documents, Strong);
        catalog.Policy.RecordInvocation(new ModelInvocation(ModelTiers.Strong, Strong, true, DisclosurePurposes.Intake, DataCategories.Documents,
            "allowed", null, null, null, "sonnet", 0, 0, "completed", clock.UtcNow));
        ScriptedModelFactory models = Models(_ => GoodUnderstanding);
        CapturedOriginal note = await Capture(catalog, "renovation.md", Encoding.UTF8.GetBytes(Notes));
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));
        Assert.Equal(UnderstandingStatuses.IndexOnly, catalog.Understanding.ReadCurrent(note.SourceId)!.Status);

        clock.UtcNow = clock.UtcNow.AddHours(2);
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));
        Assert.Empty(models.Requests.GetValueOrDefault(Strong) ?? []);

        clock.UtcNow = clock.UtcNow.AddDays(1);
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));
        Assert.Equal(UnderstandingStatuses.Understood, catalog.Understanding.ReadCurrent(note.SourceId)!.Status);
    }

    [Fact]
    public async Task AnAnswerThatFailsVerificationIsCorrectedByTheStrongestTier()
    {
        using SqliteCatalog catalog = Open();
        Grant(catalog, DataCategories.Documents, Strong);
        Grant(catalog, DataCategories.Documents, Strongest);
        ScriptedModelFactory models = Models(_ => InventedUnderstanding).Answer(Strongest, _ => GoodUnderstanding);
        CapturedOriginal note = await Capture(catalog, "notes.md", Encoding.UTF8.GetBytes(Notes));
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));

        Assert.Single(models.Requests[Strongest]);
        SourceUnderstanding understood = catalog.Understanding.ReadCurrent(note.SourceId)!;
        Assert.Equal((UnderstandingStatuses.Understood, ModelTiers.Strongest), (understood.Status, understood.Tier));
        Assert.DoesNotContain(catalog.Knowledge.ListEntities(null, "Acme", 10), entity => entity.Name == "Acme");
    }

    [Fact]
    public async Task WhenEveryTierFallsShortOnlyTheVerifiedSubsetIsKept()
    {
        using SqliteCatalog catalog = Open();
        Grant(catalog, DataCategories.Documents, Strong);
        Grant(catalog, DataCategories.Documents, Strongest);
        const string mostlyInvented = """
            {"summary":"Notes.","documentType":"notes","topics":[],"relations":[],"records":[],
             "entities":[{"name":"Alex Ortega","type":"person","quote":"Alex Ortega leads the crew."},{"name":"Zed","type":"person","quote":"Zed called"}]}
            """;
        ScriptedModelFactory models = Models(_ => mostlyInvented).Answer(Strongest, _ => mostlyInvented);
        CapturedOriginal note = await Capture(catalog, "notes.md", Encoding.UTF8.GetBytes(Notes));
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));

        SourceUnderstanding understood = catalog.Understanding.ReadCurrent(note.SourceId)!;
        Assert.Equal(UnderstandingStatuses.PartiallyVerified, understood.Status);
        Assert.Equal((1, 1), (understood.VerifiedItems, understood.RejectedItems));
        Assert.Single(catalog.Knowledge.ListEntities(null, "Alex Ortega", 10));
    }

    [Fact]
    public async Task AnIdenticalOriginalReusesTheEarlierUnderstandingWithoutModelCalls()
    {
        using SqliteCatalog catalog = Open();
        Grant(catalog, DataCategories.Documents, Strong);
        ScriptedModelFactory models = Models(_ => GoodUnderstanding);
        await Capture(catalog, "notes.md", Encoding.UTF8.GetBytes(Notes));
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));
        CapturedOriginal copy = await Capture(catalog, "copy of notes.md", Encoding.UTF8.GetBytes(Notes));
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));

        Assert.Single(models.Requests[Strong], request => TransactionIs(request, "entities"));
        SourceUnderstanding reused = catalog.Understanding.ReadCurrent(copy.SourceId)!;
        Assert.Equal(("reused", UnderstandingStatuses.Understood, 1), (reused.Tier, reused.Status, reused.Records));
        Assert.Equal(0, reused.InputTokens);
    }

    [Fact]
    public async Task ATableLayoutIsMappedOnceAndTheNextExportIsReadByCodeAlone()
    {
        using SqliteCatalog catalog = Open();
        Grant(catalog, DataCategories.Financial, Strong);
        const string mapping = """
            {"isFinancial":true,"headerRow":0,"dateColumn":0,"dateOrder":"month_first","descriptionColumns":[1],"amountColumn":2,
             "amountSign":"as_is","currency":"USD","accountLastFour":"4455","accountKind":"checking"}
            """;
        ScriptedModelFactory models = new ScriptedModelFactory()
            .Answer(Local, request => TransactionIs(request, "recordKinds")
                ? """{"category":"financial","documentType":"bank export","language":"en","gist":"Card activity.","recordKinds":["transaction"]}"""
                : "{}")
            .Answer(Strong, request => TransactionIs(request, "headerRow") ? mapping
                : TransactionIs(request, "entities") ? """{"summary":"Card activity.","documentType":"bank export","topics":[],"entities":[],"relations":[],"records":[]}"""
                : """{"annotations":[]}""");
        await Capture(catalog, "october.csv", Encoding.UTF8.GetBytes("Date,Description,Amount\n10/02/2026,MARKET FRESH,-84.12\n10/09/2026,MARKET FRESH,-61.40\n"));
        await Capture(catalog, "november.csv", Encoding.UTF8.GetBytes("Date,Description,Amount\n11/04/2026,CORNER BAKERY,-12.75\n"));
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));

        Assert.Single(models.Requests[Strong], request => TransactionIs(request, "headerRow"));
        Assert.Equal(3, new Application.Finance.FinancialLedger(catalog.Finance).Transactions(new Application.Finance.LedgerQuery(Status: null), 10).Count);
    }

    [Fact]
    public async Task WithoutAGrantOrAUsableModelTheSourceIsHeldForTheOwnerAndStaysSearchable()
    {
        using SqliteCatalog catalog = Open();
        ScriptedModelFactory models = new();
        CapturedOriginal note = await Capture(catalog, "notes.md", Encoding.UTF8.GetBytes(Notes));
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));

        SourceUnderstanding state = catalog.Understanding.ReadCurrent(note.SourceId)!;
        Assert.Equal(UnderstandingStatuses.Held, state.Status);
        Assert.Equal(DataCategories.Unclassified, state.Category);
        Assert.Single(catalog.Policy.ReadHolds(openOnly: true, 10), hold => hold.ItemId == note.ItemId);
        Assert.Single(catalog.Search.Query("Ortega"));
    }

    [Fact]
    public async Task TechnicalExportsGiveTheGraphTypedCallsWithoutAnyModel()
    {
        using SqliteCatalog catalog = Open();
        Grant(catalog, DataCategories.Unclassified, Strong);
        ScriptedModelFactory models = Models(_ => GoodUnderstanding);
        const string export = """
            {"asm":{"content":[{"type":"text","text":"File: /sample/Client.exe.bndb\n\n# Block 1 at 0x62621c\n00626231  e8 b6 15 00 00 call    CRadar_TickZoomAnimation\n00626294  ff 15 a8 40 84 00 call    dword [GameDataSetQuery]\n006262a0  ff d0 call eax\n006262a2  90 nop ; call BogusComment\n006262b0  ff 50 04 call dword [eax+0x4]"}]}}
            """;
        CapturedOriginal source = await Capture(catalog, "CRadar_Show.json", Encoding.UTF8.GetBytes(export));
        IReadOnlyList<Application.Processing.IProcessingHandler> handlers = CatalogProcessing.CreateHandlers(catalog, models, clock);
        await ProcessingDriver.RunIntakeAsync(catalog, handlers);
        Assert.True(new ProcessingDriver(catalog, handlers.Single(handler => handler.ProcessorKey == ProcessorKeys.TechnicalEvidence)).ProcessNext());
        Assert.Empty(catalog.Policy.ReadInvocations(10));
        Assert.Equal("technical_evidence", catalog.Understanding.ReadCurrent(source.SourceId)!.Reason);
        Assert.Single(catalog.Search.Query("GameDataSetQuery"));

        GraphQuery scope = new(SourceId: source.SourceId);
        GraphNode callee = Assert.Single(catalog.Graph.Seeds("GameDataSetQuery", scope).Nodes);
        Assert.Equal("function", callee.EntityType);
        GraphEdge edge = Assert.Single(catalog.Graph.Neighborhood(callee.EntityId, scope).Edges);
        Assert.Equal(("calls", "documented"), (edge.Predicate, edge.EvidenceStatus));
        Assert.Contains("call    dword [GameDataSetQuery]", edge.Evidence!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(catalog.Knowledge.ListEntities(), entity => entity.Name is "eax" or "BogusComment");
        Assert.True(catalog.Sources.Revoke(source.SourceId));
        Assert.Empty(catalog.Graph.Seeds("GameDataSetQuery", scope).Nodes);
    }

    [Fact]
    public void TechnicalNotesDistinguishClassesBinariesAndAssets()
    {
        const string source = """
            # CItem & CItemData Classes — Item System
            # CNpc Class
            struct CPlayer : CRole {
            This references `RoleView.dll`, `Conquer.exe.bndb` and `start.dat`.
            """;
        string path = Path.Combine(workspace, "notes.md");
        Assert.False(TechnicalEvidence.IsArtifact(path, source));
        var facts = TechnicalEvidence.Extract(path, source);
        Assert.Contains(facts, fact => fact is { Value: "CItem", EntityType: "class" });
        Assert.Contains(facts, fact => fact is { Value: "CItemData", EntityType: "class" });
        Assert.Contains(facts, fact => fact is { Value: "CNpc", EntityType: "class" });
        Assert.Contains(facts, fact => fact is { Value: "RoleView.dll", EntityType: "binary_module" });
        Assert.Contains(facts, fact => fact is { Value: "start.dat", EntityType: "data_asset" });
        Assert.Contains(facts, fact => fact is { Value: "inherits_from", RelationSubject: "CPlayer", RelationObject: "CRole" });
        Assert.DoesNotContain(facts, fact => fact.Value == "Conquer.exe");
        Assert.False(TechnicalEvidence.IsArtifact(Path.Combine(workspace, "settings.json"), """{"theme":"dark"}"""));
    }

    private static bool TransactionIs(ChatRequest request, string property) => Finance.TransactionIntakeTests.Is(request, property);

    private SqliteCatalog Open()
    {
        SqliteCatalog catalog = SqliteCatalog.Open(Path.Combine(workspace, "store"), clock);
        catalog.Verification.SaveProvider(new VerificationProvider(Local, null, "qwen:9b", 0, 500, true, "http://127.0.0.1:11434"));
        catalog.Verification.SaveProvider(new VerificationProvider(Strong, "/usr/bin/true", "sonnet", 10, 500, true));
        catalog.Verification.SaveProvider(new VerificationProvider(Strongest, "/usr/bin/true", "gpt", 20, 500, true));
        catalog.Policy.SaveTier(ModelTiers.Local, Local);
        catalog.Policy.SaveTier(ModelTiers.Strong, Strong);
        catalog.Policy.SaveTier(ModelTiers.Strongest, Strongest);
        return catalog;
    }

    private static void Grant(SqliteCatalog catalog, string category, string provider) =>
        new PolicyService(catalog.Policy).Request(new PolicyChange(AddRule: new DisclosureRule(Guid.NewGuid(), category, provider,
            DisclosurePurposes.Intake, true, null)), PolicyActors.Owner);

    private static ScriptedModelFactory Models(Func<ChatRequest, string> strong) =>
        new ScriptedModelFactory()
            .Answer(Local, request => TransactionIs(request, "recordKinds") ? Triage : "{}")
            .Answer(Strong, strong);

    private async Task<CapturedOriginal> Capture(SqliteCatalog catalog, string name, byte[] bytes)
    {
        using MemoryStream input = new(bytes);
        return await catalog.Originals.CaptureFileAsync(Path.Combine(workspace, "incoming", name), input, bytes.Length, name);
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }
}
