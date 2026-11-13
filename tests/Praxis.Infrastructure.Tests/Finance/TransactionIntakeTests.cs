using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Application.Finance;
using Praxis.Application.Inference;
using Praxis.Application.Policy;
using Praxis.Domain.Finance;
using Praxis.Domain.Models;
using Praxis.Domain.Originals;
using Praxis.Domain.Policy;
using Praxis.Domain.Processing;
using Praxis.Domain.Understanding;
using Praxis.Domain.Verification;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Finance;

/// <summary>
/// The transaction profile inside the generic intake engine (ADR 0044) under the disclosure policy:
/// published formats by code, tables mapped once, statements transcribed and proven, holds and fallbacks.
/// </summary>
public sealed class TransactionIntakeTests : IDisposable
{
    private const string Local = VerificationProvider.Ollama;
    private const string Strong = VerificationProvider.ClaudeCode;
    private const string Strongest = VerificationProvider.Codex;

    private const string CheckingCsv = """
        Date,Description,Amount,Status
        10/03/2026,SAFEWAY #1234 SEATTLE WA,-54.21,Posted
        10/13/2026,NETFLIX.COM,-15.99,Posted
        10/25/2026,SHELL OIL 57444,-40.00,Posted
        10/28/2026,LYFT RIDE,-12.00,Pending
        """;

    private const string CheckingMapping = """
        {"isFinancial":true,"headerRow":0,"dateColumn":0,"dateOrder":"month_first","descriptionColumns":[1],"amountColumn":2,
         "amountSign":"as_is","statusColumn":3,"currency":"USD","accountLastFour":"1234","accountKind":"checking"}
        """;

    private const string CardTranscription = """
        {"isStatement":true,"accountKind":"card","accountLastFour":"9876","openingBalance":"1,200.00","closingBalance":"345.10","transactions":[
          {"date":"2026-10-15","description":"PAYMENT THANK YOU","amount":"1,000.00"},
          {"date":"2026-10-18","description":"AMAZON RETURN","amount":"25.00"},
          {"date":"2026-10-03","description":"CHIPOTLE 0921","amount":"-32.50"},
          {"date":"2026-10-07","description":"UBER TRIP","amount":"-18.40"},
          {"date":"2026-10-21","description":"WHOLE FOODS MARKET","amount":"-96.12"},
          {"date":"2026-10-28","description":"SPOTIFY USA","amount":"-11.99"},
          {"date":"2026-10-30","description":"LATE FEE","amount":"-11.09"}]}
        """;

    private readonly string workspace = Path.Combine(TestPaths.Temp, "praxis-intake-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));

    public TransactionIntakeTests()
    {
        root = Path.Combine(workspace, "store");
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(workspace, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task GrantedStrongTierDoesTheHeavyWorkWhileTheLocalTierOnlyTriages()
    {
        using SqliteCatalog catalog = Open();
        Grant(catalog, DataCategories.Financial, Strong);
        ScriptedModelFactory models = Models();
        await Capture(catalog, "download.qfx", StructuredExportParserTests.SgmlOfx);
        await Capture(catalog, "Checking1234_october.csv", CheckingCsv);
        await Capture(catalog, "card-october.pdf", StatementFixtures.Pdf(StatementFixtures.CreditCard));
        await ProcessAll(catalog, models);

        // The OFX export is triaged by its format; only the CSV and the PDF need the local tier, once each.
        Assert.Equal(2, models.Count(Local));
        Assert.All(models.Requests[Local], request => Assert.True(Is(request, "recordKinds")));
        Assert.Equal(0, models.Count(Strongest));
        Assert.Equal(3, models.Requests[Strong].Count(request => Is(request, "entities")));
        Assert.Single(models.Requests[Strong], request => Is(request, "headerRow"));
        Assert.Single(models.Requests[Strong], request => Is(request, "isStatement"));

        FinancialLedger ledger = new(catalog.Finance);
        IReadOnlyList<LedgerTransaction> checking = ledger.Transactions(new LedgerQuery(AccountKey: "acct-1234", Status: null), 100);
        Assert.Equal(6, checking.Count);
        LedgerTransaction safeway = Assert.Single(checking, transaction => transaction.Description.StartsWith("SAFEWAY", StringComparison.Ordinal));
        Assert.Equal(2, safeway.Evidence.Count);
        Assert.Equal(SpendingCategories.Groceries, safeway.Category);
        Assert.Equal(ModelTiers.Strong, safeway.CategoryTier);
        Assert.Equal("Safeway", safeway.Merchant);
        Assert.Equal(new DateOnly(2026, 10, 12), Assert.Single(checking, transaction => transaction.Description.Contains("NETFLIX", StringComparison.Ordinal)).PostedOn);

        IReadOnlyList<LedgerAggregate> byCategory = ledger.Aggregate(new LedgerQuery(From: new DateOnly(2026, 10, 1), To: new DateOnly(2026, 10, 31)), LedgerGrouping.Category);
        Assert.Equal(250000L, byCategory.Single(group => group.Key == SpendingCategories.Income).TotalMinor);
        Assert.Equal(-15033L, byCategory.Single(group => group.Key == SpendingCategories.Groceries).TotalMinor);
        LedgerAggregate month = Assert.Single(ledger.Aggregate(new LedgerQuery(From: new DateOnly(2026, 10, 1), To: new DateOnly(2026, 10, 31)), LedgerGrouping.Month));
        Assert.Equal("2026-10", month.Key);
        Assert.Equal(352500L, month.InflowMinor);
        Assert.Equal(1, ledger.Aggregate(new LedgerQuery(Status: TransactionStatuses.Pending), LedgerGrouping.None).Single().Count);

        LedgerAccount card = Assert.Single(ledger.Accounts(), account => account.AccountKey == "acct-9876");
        Assert.Equal(-34510L, card.LatestBalance?.Minor);
        Assert.Contains(catalog.Policy.ReadInvocations(50), invocation => invocation.Remote && invocation.ProviderId == Strong &&
                                                                         invocation.Category == DataCategories.Financial && invocation.DecisionReason == DisclosureDecision.GrantedByRule);
    }

    [Fact]
    public async Task WithoutAGrantALocallyProvenResultIsKeptAndOtherwiseTheItemIsHeldUntilTheOwnerDecides()
    {
        using SqliteCatalog catalog = Open();
        const string provable = """
            Date,Description,Amount,Balance
            11/01/2026,COFFEE,-5.00,95.00
            11/02/2026,PAYROLL,1000.00,1095.00
            """;
        ScriptedModelFactory models = Models(localAnswer: request => Is(request, "headerRow")
            ? """{"isFinancial":true,"headerRow":0,"dateColumn":0,"dateOrder":"month_first","descriptionColumns":[1],"amountColumn":2,"amountSign":"as_is","balanceColumn":3,"currency":"USD","accountKind":"checking"}"""
            : Is(request, "isStatement") ? "{}"
            : Is(request, "recordKinds") ? Triage
            : Is(request, "entities") ? Understanding
            : """{"annotations":[]}""");
        CapturedOriginal csv = await Capture(catalog, "savings-7788.csv", provable);
        CapturedOriginal statement = await Capture(catalog, "card-october.pdf", StatementFixtures.Pdf(StatementFixtures.CreditCard));
        await ProcessAll(catalog, models);

        Assert.Equal(0, models.Count(Strong));
        FinancialLedger ledger = new(catalog.Finance);
        FinancialDocumentSummary local = Assert.Single(ledger.Documents(10), document => document.ItemId == csv.ItemId);
        Assert.Equal("completed", local.Outcome);
        Assert.Equal(2, ledger.Transactions(new LedgerQuery()).Count);

        // The statement waits for approval; the locally proven export's annotations wait too.
        IReadOnlyList<DisclosureHold> holds = catalog.Policy.ReadHolds(openOnly: true, 10);
        Assert.Contains(holds, item => item.ItemId == csv.ItemId && item.ProcessorKey == ProcessorKeys.IntakeUnderstand);
        DisclosureHold hold = Assert.Single(holds, item => item.ItemId == statement.ItemId);
        Assert.Equal(statement.ItemId, hold.ItemId);
        Assert.Equal(Strong, hold.ProviderId);
        Assert.Equal(DataCategories.Financial, hold.Category);
        Assert.Equal(clock.GetUtcNow().AddHours(72), hold.DeadlineUtc);
        Assert.Equal(UnderstandingStatuses.Held, catalog.Understanding.ReadCurrent(statement.SourceId)?.Status);
        Assert.DoesNotContain(ledger.Documents(10), document => document.ItemId == statement.ItemId);

        Assert.NotNull(catalog.Policy.DecideHold(hold.HoldId, HoldDecisions.ApproveOnce));
        await ProcessAll(catalog, models);
        Assert.Single(models.Requests[Strong], request => Is(request, "isStatement"));
        Assert.Equal("completed", Assert.Single(ledger.Documents(10), document => document.ItemId == statement.ItemId).Outcome);
        Assert.DoesNotContain(catalog.Policy.ReadHolds(openOnly: true, 10), item => item.ItemId == statement.ItemId);
        Assert.Empty(new PolicyService(catalog.Policy).Current().Rules);
    }

    [Fact]
    public async Task GrantingFromAHoldCreatesAStandingRuleAndUnansweredHoldsExpireToIndexOnly()
    {
        using SqliteCatalog catalog = Open();
        ScriptedModelFactory models = Models(localAnswer: request => Is(request, "recordKinds") ? Triage : "{}");
        CapturedOriginal first = await Capture(catalog, "first.pdf", StatementFixtures.Pdf(StatementFixtures.CreditCard));
        await ProcessAll(catalog, models);
        DisclosureHold hold = Assert.Single(catalog.Policy.ReadHolds(openOnly: true, 10));
        Assert.Equal(ProcessorKeys.IntakeUnderstand, hold.ProcessorKey);
        catalog.Policy.DecideHold(hold.HoldId, HoldDecisions.GrantCategory);
        DisclosureRule rule = Assert.Single(new PolicyService(catalog.Policy).Current().Rules);
        Assert.Equal((DataCategories.Financial, Strong, true), (rule.Category, rule.ProviderId, rule.Allow));

        new PolicyService(catalog.Policy).Request(new PolicyChange(RemoveRuleId: rule.RuleId), PolicyActors.Owner);
        CapturedOriginal second = await Capture(catalog, "second.pdf", StatementFixtures.Pdf(StatementFixtures.SectionedChecking));
        await ProcessAll(catalog, models);
        Assert.Single(catalog.Policy.ReadHolds(openOnly: true, 10), item => item.ItemId == second.ItemId);

        clock.UtcNow = clock.UtcNow.AddHours(73);
        await ProcessAll(catalog, models);
        Assert.Empty(catalog.Policy.ReadHolds(openOnly: true, 10));
        Assert.Equal(UnderstandingStatuses.IndexOnly, catalog.Understanding.ReadCurrent(second.SourceId)?.Status);
        Assert.DoesNotContain(new FinancialLedger(catalog.Finance).Documents(10), document => document.ItemId == second.ItemId);
        Assert.Equal("completed", Assert.Single(new FinancialLedger(catalog.Finance).Documents(10), document => document.ItemId == first.ItemId).Outcome);
    }

    [Fact]
    public async Task TheStrongestTierIsAskedOnlyWhenTheStrongAnswerFailsVerification()
    {
        using SqliteCatalog catalog = Open();
        Grant(catalog, DataCategories.Financial, Strong);
        Grant(catalog, DataCategories.Financial, Strongest);
        ScriptedModelFactory models = Models(strongAnswer: request => Is(request, "isStatement")
            ? CardTranscription.Replace("\"-96.12\"", "\"-69.12\"", StringComparison.Ordinal)
            : Is(request, "entities") ? Understanding : """{"annotations":[]}""");
        models.Answer(Strongest, request => Is(request, "isStatement") ? CardTranscription : """{"annotations":[]}""");
        await Capture(catalog, "card.pdf", StatementFixtures.Pdf(StatementFixtures.CreditCard));
        await ProcessAll(catalog, models);

        Assert.Single(models.Requests[Strongest], request => Is(request, "isStatement"));
        FinancialDocumentSummary document = Assert.Single(new FinancialLedger(catalog.Finance).Documents(10));
        Assert.Equal("completed", document.Outcome);
        Assert.Equal(7, document.TransactionCount);
    }

    [Fact]
    public async Task LocalOnlySourcesNeverLeaveTheMachineEvenUnderAllowAll()
    {
        using SqliteCatalog catalog = Open();
        new PolicyService(catalog.Policy).Request(new PolicyChange(Mode: DisclosureModes.AllowAll), PolicyActors.Owner);
        new PolicyService(catalog.Policy).Request(new PolicyChange(FallbackCategory: DataCategories.Financial, FallbackChain: [FallbackSteps.IndexOnly]), PolicyActors.Owner);
        CapturedOriginal source = await Capture(catalog, "private.pdf", StatementFixtures.Pdf(StatementFixtures.CreditCard));
        catalog.Policy.SaveCategory(new SourceCategory(source.SourceId, DataCategories.Financial, SourceCategory.OwnerOrigin, LocalOnly: true));
        ScriptedModelFactory models = Models();
        await ProcessAll(catalog, models);

        Assert.Equal(0, models.Count(Strong));
        Assert.Equal(0, models.Count(Strongest));
        Assert.Equal(UnderstandingStatuses.IndexOnly, catalog.Understanding.ReadCurrent(source.SourceId)?.Status);
        Assert.Empty(new FinancialLedger(catalog.Finance).Documents(10));
    }

    [Fact]
    public async Task RevocationPurgesRowsAndAnnotationsWhileCorroboratingDocumentsRemain()
    {
        using SqliteCatalog catalog = Open();
        Grant(catalog, DataCategories.Financial, Strong);
        ScriptedModelFactory models = Models();
        CapturedOriginal ofx = await Capture(catalog, "download.qfx", StructuredExportParserTests.SgmlOfx);
        await Capture(catalog, "Checking1234_october.csv", CheckingCsv);
        await ProcessAll(catalog, models);

        Assert.True(catalog.Sources.Revoke(ofx.SourceId));
        IReadOnlyList<LedgerTransaction> remaining = new FinancialLedger(catalog.Finance).Transactions(new LedgerQuery(Status: null), 100);
        Assert.DoesNotContain(remaining, transaction => transaction.Description.Contains("PAYROLL", StringComparison.Ordinal));
        Assert.Equal(FinancialFormats.Delimited,
            Assert.Single(Assert.Single(remaining, transaction => transaction.Description.StartsWith("SAFEWAY", StringComparison.Ordinal)).Evidence).Format);
        Assert.Equal(0L, Count(catalog, "SELECT COUNT(*) FROM financial_transactions WHERE item_id = $item", ofx.ItemId));
        Assert.Equal(0L, Count(catalog, "SELECT COUNT(*) FROM financial_annotations WHERE item_id = $item", ofx.ItemId));
    }

    [Fact]
    public async Task NonFinancialVerdictsAndUnconfiguredTiersNeverFabricateRows()
    {
        using SqliteCatalog catalog = Open();
        Grant(catalog, DataCategories.Unclassified, Strong);
        ScriptedModelFactory models = new ScriptedModelFactory()
            .Answer(Strong, request => Is(request, "headerRow") ? """{"isFinancial":false}""" : Is(request, "entities") ? Understanding : "{}");
        await Capture(catalog, "people.csv", "name,email\nAda,ada@example.com\nGrace,grace@example.com\n");
        await Capture(catalog, "notes.txt", "10/01/2026,payroll,2500.00");
        await ProcessAll(catalog, models);

        // Without a usable local tier both stay unclassified; nothing suggests money, so no table mapping is even asked for.
        Assert.Empty(new FinancialLedger(catalog.Finance).Documents(10));
        Assert.DoesNotContain(models.Requests[Strong], request => Is(request, "headerRow") || Is(request, "isStatement"));
        Assert.Equal(2, models.Count(Strong));
    }

    private SqliteCatalog Open()
    {
        SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
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

    private const string Triage = """{"category":"financial","documentType":"statement","language":"en","gist":"An account statement.","recordKinds":["transaction"]}""";
    private const string Understanding = """{"summary":"An account statement.","documentType":"statement","topics":[],"entities":[],"relations":[],"records":[]}""";

    /// <summary>True when the request's response schema has the property that identifies its step.</summary>
    internal static bool Is(ChatRequest request, string property) =>
        request.ResponseSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(property, out _);

    /// <summary>Strong tier answers by schema: understanding, mapping, transcription or annotations; local tier triages.</summary>
    private static ScriptedModelFactory Models(Func<ChatRequest, string>? strongAnswer = null, Func<ChatRequest, string>? localAnswer = null) =>
        new ScriptedModelFactory()
            .Answer(Local, localAnswer ?? (request => Is(request, "recordKinds") ? Triage : "{}"))
            .Answer(Strong, strongAnswer ?? (request =>
                Is(request, "headerRow") ? CheckingMapping
                : Is(request, "isStatement") ? CardTranscription
                : Is(request, "entities") ? Understanding
                : Annotations(request.Messages[^1].Content)));

    /// <summary>Deterministic annotations keyed by description, standing in for a strong model.</summary>
    private static string Annotations(string rows)
    {
        StringBuilder items = new();
        foreach (string line in rows.Split('\n').Where(line => line.Contains(" | ", StringComparison.Ordinal)))
        {
            string[] parts = line.Split(" | ");
            string description = parts[2];
            (string merchant, string category) = description switch
            {
                _ when description.Contains("SAFEWAY", StringComparison.Ordinal) => ("Safeway", SpendingCategories.Groceries),
                _ when description.Contains("WHOLE FOODS", StringComparison.Ordinal) => ("Whole Foods", SpendingCategories.Groceries),
                _ when description.Contains("PAYROLL", StringComparison.Ordinal) => ("Acme Corp", SpendingCategories.Income),
                _ when description.Contains("NETFLIX", StringComparison.Ordinal) => ("Netflix", SpendingCategories.Subscriptions),
                _ when description.Contains("PAYMENT THANK YOU", StringComparison.Ordinal) => ("Card payment", SpendingCategories.Transfer),
                _ => (description.Split(' ')[0], SpendingCategories.Uncategorized)
            };
            items.Append(items.Length == 0 ? "" : ",").Append($$"""{"id":{{parts[0]}},"merchant":"{{merchant}}","category":"{{category}}"}""");
        }

        return $$"""{"annotations":[{{items}}]}""";
    }

    private async Task ProcessAll(SqliteCatalog catalog, ScriptedModelFactory models)
    {
        await ProcessingDriver.RunIntakeAsync(catalog, CatalogProcessing.CreateHandlers(catalog, models, clock));
    }

    private Task<CapturedOriginal> Capture(SqliteCatalog catalog, string name, string text) => Capture(catalog, name, Encoding.UTF8.GetBytes(text));

    private async Task<CapturedOriginal> Capture(SqliteCatalog catalog, string name, byte[] bytes)
    {
        using MemoryStream input = new(bytes);
        return await catalog.Originals.CaptureFileAsync(Path.Combine(workspace, "incoming", name), input, bytes.Length, name);
    }

    private static long Count(SqliteCatalog catalog, string sql, Guid itemId)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = catalog.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$item", itemId.ToString("D"));
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }
}
