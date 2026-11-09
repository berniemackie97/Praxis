using System.Text;
using System.Text.Json;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalFinancialEvidenceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "praxis-budget-" + Guid.NewGuid().ToString("N"));

    public LocalFinancialEvidenceTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task OriginalVerifiedTotalsAndScenariosSurviveRestartWithoutRewritingSpending()
    {
        const string csv = """
            date,record_id,kind,amount,currency,category,description,status
            2026-11-01,pay,income,1000.00,USD,Income,Salary,posted
            2026-11-02,food,expense,200.00,USD,Food,Groceries,posted
            2026-11-03,phone,expense,100.00,USD,Utilities,Phone,posted
            2026-11-04,rent,bill,50.00,USD,Housing,Unpaid rent,estimated
            2026-11-05,pending,expense,75.00,USD,Food,Unsettled,pending
            2026-11-06,transfer,transfer,500.00,USD,Transfer,Own accounts,posted
            2026-11-07,refund,refund,20.00,USD,Food,Returned item,posted
            2026-11-07,refund,refund,20.00,USD,Food,Returned item,posted
            2026-10-01,previous,expense,900.00,USD,Other,Prior month,posted
            """;
        CapturedOriginal saved;
        using (var catalog = LocalCatalog.Open(root))
        {
            saved = await Capture(catalog, "budget.csv", csv, "budget");
            var report = await catalog.Finance.AnalyzeAsync(saved.SourceId, 2026, 11);
            Assert.Equal(saved.SourceId, report.SourceId);
            Assert.Equal(saved.ItemId, report.ItemId);
            Assert.Equal(saved.Sha256, report.SourceSha256);
            Assert.Equal(1, report.Revision);
            Assert.Equal(1000m, report.PostedIncome);
            Assert.Equal(280m, report.PostedSpending);
            Assert.Equal(50m, report.PlannedBills);
            Assert.Equal(720m, report.ActualBalance);
            Assert.Equal(670m, report.BalanceAfterPlannedBills);
            Assert.Equal(75m, report.PendingNetSpending);
            Assert.Equal(1, report.IgnoredDuplicateRows);
            Assert.Equal(1, report.TransfersExcluded);
            Assert.Equal(1, report.PendingRows);
            Assert.Equal(180m, Assert.Single(report.Categories,
                x => x.Category == "Food").PostedSpending);
            Assert.Equal(8, Assert.Single(report.Evidence,
                x => x.RecordId == "refund").SourceRow);
            Assert.Empty(catalog.Search.Query("Salary"));

            var scenario = await catalog.Finance.PreviewAsync(saved.SourceId, 2026, 11,
                [new MonthlyBudgetTarget("Food", 120m)]);
            Assert.Equal(60m, scenario.IllustrativeReduction);
            Assert.Equal(730m, scenario.HypotheticalNetAfterPlannedBills);
            Assert.Equal(280m, scenario.PostedSpending);
            Assert.Equal(50m, scenario.PlannedBills);
            Assert.Equal(1, scenario.PendingRows);
            await catalog.Originals.VerifyAsync(saved.ItemId);
        }

        using var reopened = LocalCatalog.Open(root);
        var afterRestart = await reopened.Finance.AnalyzeAsync(saved.SourceId, 2026, 11);
        Assert.Equal(670m, afterRestart.BalanceAfterPlannedBills);
        Assert.Equal(saved.Sha256, afterRestart.SourceSha256);
        Assert.Equal(0m, (await reopened.Finance.AnalyzeAsync(
            saved.SourceId, 2026, 9)).PostedIncome);
    }

    [Theory]
    [InlineData("2026-11-01,same,income,100.00,USD,Income,Salary,posted\n2026-11-01,same,income,200.00,USD,Income,Salary,posted")]
    [InlineData("2026-11-01,one,expense,-9.00,USD,Food,Expense,posted")]
    [InlineData("2026-11-01,one,expense,1.001,USD,Food,Expense,posted")]
    [InlineData("2026-11-01,one,expense,1.00,EUR,Food,Expense,posted")]
    [InlineData("2026-11-31,one,expense,1.00,USD,Food,Expense,posted")]
    [InlineData("2026-11-01,one,bill,1.00,USD,Rent,Rent,posted")]
    [InlineData("2026-11-01,one,expense,1.00,USD,Rent,\"unclosed,posted")]
    public async Task RejectsAmbiguousOrMalformedFinancialEvidence(string row)
    {
        using var catalog = LocalCatalog.Open(root);
        var saved = await Capture(catalog, "invalid.csv",
            "date,record_id,kind,amount,currency,category,description,status\n" + row,
            "malformed");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.Finance.AnalyzeAsync(saved.SourceId, 2026, 11));
        await catalog.Originals.VerifyAsync(saved.ItemId);
    }

    [Fact]
    public async Task QuotedMultilineFieldsRetainPhysicalRowEvidence()
    {
        const string csv = "date,record_id,kind,amount,currency,category,description,status\r\n" +
            "2026-11-01,one,expense,10.00,USD,Home,\"Two-line\nreceipt\",posted\r\n" +
            "2026-11-02,two,expense,5.00,USD,Home,Second purchase,posted\r\n";
        using var catalog = LocalCatalog.Open(root);
        var saved = await Capture(catalog, "multiline.csv", csv, "quoted");
        var report = await catalog.Finance.AnalyzeAsync(saved.SourceId, 2026, 11);
        Assert.Equal(15m, report.PostedSpending);
        Assert.Equal(new[] { 2, 4 }, report.Evidence.Select(x => x.SourceRow).ToArray());
        Assert.Equal("Two-line\nreceipt", report.Evidence[0].Description);
    }

    [Fact]
    public async Task RevokedUpdatedCorruptOrNonFinancialSourcesCannotBeRead()
    {
        using var catalog = LocalCatalog.Open(root);
        var original = await Capture(catalog, "changed.csv",
            "date,record_id,kind,amount,currency,category,description,status\n" +
            "2026-11-01,one,income,100.00,USD,Income,Old,posted\n", "first");
        var changed = await Capture(catalog, "changed.csv",
            "date,record_id,kind,amount,currency,category,description,status\n" +
            "2026-11-01,two,income,200.00,USD,Income,New,posted\n", "second");
        Assert.Equal(original.SourceId, changed.SourceId);
        Assert.Equal(original.Revision + 1, changed.Revision);
        var updated = await catalog.Finance.AnalyzeAsync(original.SourceId, 2026, 11);
        Assert.Equal(changed.Sha256, updated.SourceSha256);
        Assert.Equal(200m, updated.PostedIncome);
        Assert.True(catalog.Sources.Revoke(original.SourceId));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => catalog.Finance.AnalyzeAsync(original.SourceId, 2026, 11));

        var invalidType = await Capture(catalog, "not-financial.md",
            "date,record_id,kind,amount,currency,category,description,status", "markdown");
        await Assert.ThrowsAsync<ArgumentException>(
            () => catalog.Finance.AnalyzeAsync(invalidType.SourceId, 2026, 11));

        var corrupted = await Capture(catalog, "corrupt.csv",
            "date,record_id,kind,amount,currency,category,description,status\n",
            "corrupt");
        var target = Path.Combine(root, "objects", corrupted.Sha256[..2],
            corrupted.Sha256[2..4], corrupted.Sha256);
        File.WriteAllBytes(target, Encoding.UTF8.GetBytes("tampered"));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => catalog.Finance.AnalyzeAsync(corrupted.SourceId, 2026, 11));
    }

    [Fact]
    public async Task ScenarioTargetsAreBoundedUniqueAndNeverExceedObservedSpending()
    {
        using var catalog = LocalCatalog.Open(root);
        var saved = await Capture(catalog, "scenario.csv",
            "date,record_id,kind,amount,currency,category,description,status\n" +
            "2026-11-01,pay,income,400.00,USD,Income,Salary,posted\n" +
            "2026-11-02,food,expense,70.00,USD,Food,Meal,posted\n", "scenario");
        MonthlyBudgetTarget[][] invalid =
        [
            [new("Missing", 10m)],
            [new("Food", 71m)],
            [new("Food", -1m)],
            [new("Food", 20.001m)],
            [new("Food", 10m), new("Food", 10m)]
        ];
        foreach (var targets in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                catalog.Finance.PreviewAsync(saved.SourceId, 2026, 11, targets));
        }

        var baseline = await catalog.Finance.PreviewAsync(
            saved.SourceId, 2026, 11, []);
        Assert.Equal(baseline.BaselineNetAfterPlannedBills,
            baseline.HypotheticalNetAfterPlannedBills);
        Assert.Equal(0m, baseline.IllustrativeReduction);
    }

    [Fact]
    public async Task PrivateDesktopReturnsCitedReportAndScenarioAndDeniesMalformedInputs()
    {
        using var catalog = LocalCatalog.Open(root);
        var saved = await Capture(catalog, "protocol.csv",
            "date,record_id,kind,amount,currency,category,description,status\n" +
            "2026-11-01,pay,income,400.00,USD,Income,Salary,posted\n" +
            "2026-11-02,food,expense,70.00,USD,Food,Meal,posted\n", "protocol");
        var protocol = new LocalDesktopProtocol(catalog);
        using (var report = JsonDocument.Parse(await protocol.HandleAsync($$"""
            {"requestId":"summary","action":"budgetSummary","sourceId":"{{saved.SourceId:D}}","year":2026,"month":11}
            """)))
        {
            Assert.True(report.RootElement.GetProperty("ok").GetBoolean());
            var budget = report.RootElement.GetProperty("data").GetProperty("budget");
            Assert.Equal(saved.Sha256, budget.GetProperty("sourceSha256").GetString());
            Assert.Equal(saved.ItemId.ToString("D"), budget.GetProperty("itemId").GetString());
            Assert.Equal(330m, budget.GetProperty("actualBalance").GetDecimal());
        }

        using (var planned = JsonDocument.Parse(await protocol.HandleAsync($$"""
            {"requestId":"scenario","action":"budgetScenario","sourceId":"{{saved.SourceId:D}}","year":2026,"month":11,"targets":[{"category":"Food","target":"40.00"}]}
            """)))
        {
            Assert.True(planned.RootElement.GetProperty("ok").GetBoolean());
            var result = planned.RootElement.GetProperty("data").GetProperty("scenario");
            Assert.Equal(30m, result.GetProperty("illustrativeReduction").GetDecimal());
            Assert.Equal(360m, result.GetProperty("hypotheticalNetAfterPlannedBills").GetDecimal());
        }

        foreach (var payload in new[]
        {
            $$"""{"requestId":"bad","action":"budgetScenario","sourceId":"{{saved.SourceId:D}}","year":2026,"month":11,"targets":[{"category":"Food","target":"-12.00"}]}""",
            $$"""{"requestId":"bad","action":"budgetScenario","sourceId":"{{saved.SourceId:D}}","year":2026,"month":11,"targets":[{"category":"Food","target":40.00}]}""",
            $$"""{"requestId":"bad","action":"budgetScenario","sourceId":"{{saved.SourceId:D}}","year":2026,"month":11,"targets":{}}""",
            $$"""{"requestId":"bad","action":"budgetSummary","sourceId":"{{saved.SourceId:D}}","year":2026,"month":13}"""
        })
        {
            using var invalid = JsonDocument.Parse(await protocol.HandleAsync(payload));
            Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(invalid.RootElement.TryGetProperty("data", out _));
        }

        Assert.True(catalog.Sources.Revoke(saved.SourceId));
        using var revoked = JsonDocument.Parse(await protocol.HandleAsync($$"""
            {"requestId":"revoked","action":"budgetSummary","sourceId":"{{saved.SourceId:D}}","year":2026,"month":11}
            """));
        Assert.False(revoked.RootElement.GetProperty("ok").GetBoolean());
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string name, string text, string key)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var stream = new MemoryStream(bytes);
        return await catalog.Originals.CaptureFileAsync(
            Path.Combine(root, "incoming", name), stream, bytes.Length + 1, key);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
