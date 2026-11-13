using System.Text;
using Praxis.Application.Inference;
using Praxis.Application.Policy;
using Praxis.Domain.Models;
using Praxis.Domain.Originals;
using Praxis.Domain.Policy;
using Praxis.Domain.Understanding;
using Praxis.Domain.Verification;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
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

        Assert.Single(models.Requests[Local]);
        Assert.Single(models.Requests[Strong]);
        Assert.Equal(0, models.Count(Strongest));
        SourceUnderstanding understood = catalog.Understanding.ReadCurrent(note.SourceId)!;
        Assert.Equal((UnderstandingStatuses.Understood, "docx", ModelTiers.Strong), (understood.Status, understood.Format, understood.Tier));
        Assert.Equal(("meeting notes", DataCategories.Documents, 1), (understood.DocumentType, understood.Category, understood.Records));
        Assert.Equal("Notes on the kitchen renovation and the site visit.", understood.Summary);
        Assert.Contains(catalog.Knowledge.ListEntities(null, "Ortega Renovations", 10), entity => entity.EntityType == "organization");
        Assert.Contains(catalog.Policy.ReadInvocations(10), call => call.ProviderId == Local && !call.Remote);
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
