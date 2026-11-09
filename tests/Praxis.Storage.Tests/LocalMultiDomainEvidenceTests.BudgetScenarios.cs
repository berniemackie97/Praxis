using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed partial class LocalMultiDomainEvidenceTests
{
    [Fact]
    public async Task OwnerBudgetTargetsProduceIllustrativeTotalsWithoutRewritingObservedSpending()
    {
        const string csv = """
            date,record_id,kind,amount,currency,category,description,status
            2026-11-01,salary,income,1000.00,USD,Income,Salary,posted
            2026-11-02,food,expense,200.00,USD,Food,Groceries,posted
            2026-11-03,phone,expense,100.00,USD,Utilities,Phone,posted
            2026-11-04,rent,bill,50.00,USD,Housing,Upcoming rent payment,estimated
            2026-11-05,pending,expense,75.00,USD,Food,Unsettled order,pending
            """;
        CapturedOriginal original;
        using (var catalog = LocalCatalog.Open(dataRoot))
        {
            original = await Capture(catalog, "budget.csv", csv, "scenario-budget");
            var planner = new LocalBudgetScenarioPlanner(catalog);
            var preview = await planner.PreviewAsync(original.SourceId, 2026, 11,
                [new MonthlyBudgetTarget("Food", 125.00m)]);

            Assert.Equal(1000m, preview.PostedIncome);
            Assert.Equal(300m, preview.PostedSpending);
            Assert.Equal(50m, preview.PlannedBills);
            Assert.Equal(650m, preview.BaselineNetAfterPlannedBills);
            Assert.Equal(75m, preview.IllustrativeReduction);
            Assert.Equal(725m, preview.HypotheticalNetAfterPlannedBills);
            Assert.Equal(1, preview.PendingRows);
            Assert.Equal(original.Sha256, preview.SourceSha256);
            var food = Assert.Single(preview.Categories,
                category => category.Category == "Food");
            Assert.Equal(200m, food.ObservedSpending);
            Assert.Equal(125m, food.HypotheticalTarget);
            Assert.Equal(75m, food.IllustrativeReduction);
            Assert.Equal(100m, Assert.Single(preview.Categories,
                category => category.Category == "Utilities").HypotheticalTarget);

            var unchanged = await new LocalMonthlyBudgetAnalyzer(catalog)
                .AnalyzeAsync(original.SourceId, 2026, 11);
            Assert.Equal(300m, unchanged.PostedSpending);
            Assert.Equal(650m, unchanged.BalanceAfterPlannedBills);
            await catalog.VerifyOriginalAsync(original.ItemId);
        }

        using var reopened = LocalCatalog.Open(dataRoot);
        var repeated = await new LocalBudgetScenarioPlanner(reopened)
            .PreviewAsync(original.SourceId, 2026, 11,
                [new MonthlyBudgetTarget("Food", 125.00m)]);
        Assert.Equal(725m, repeated.HypotheticalNetAfterPlannedBills);
    }

    [Fact]
    public async Task BudgetScenariosRejectUnknownDuplicateAndInvalidCaps()
    {
        const string csv = """
            date,record_id,kind,amount,currency,category,description,status
            2026-11-01,pay,income,100.00,USD,Income,Pay,posted
            2026-11-02,food,expense,60.00,USD,Food,Meal,posted
            """;
        using var catalog = LocalCatalog.Open(dataRoot);
        var original = await Capture(catalog, "scenario.csv", csv, "scenario");
        var planner = new LocalBudgetScenarioPlanner(catalog);
        MonthlyBudgetTarget[][] invalid =
        [
            [new("Other", 30m)],
            [new("Food", 61m)],
            [new("Food", -1m)],
            [new("Food", 20.001m)],
            [new("Food", 30m), new("Food", 30m)]
        ];
        foreach (var targets in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => planner.PreviewAsync(original.SourceId, 2026, 11, targets));
        }

        var baseline = await planner.PreviewAsync(original.SourceId, 2026, 11, []);
        Assert.Equal(baseline.BaselineNetAfterPlannedBills,
            baseline.HypotheticalNetAfterPlannedBills);
        Assert.Equal(0m, baseline.IllustrativeReduction);
        Assert.True(catalog.RevokeSource(original.SourceId));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => planner.PreviewAsync(original.SourceId, 2026, 11, []));
    }

    [Fact]
    public async Task PrivateBudgetScenarioProtocolValidatesTargetsAndReturnsSourceBoundResult()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var source = await Capture(catalog, "plan.csv",
            "date,record_id,kind,amount,currency,category,description,status\n" +
            "2026-11-01,pay,income,400.00,USD,Income,Salary,posted\n" +
            "2026-11-02,food,expense,70.00,USD,Food,Meal,posted\n",
            "plan");
        var protocol = new LocalDesktopProtocol(catalog);
        using (var response = System.Text.Json.JsonDocument.Parse(
            await protocol.HandleAsync($$"""
                {"requestId":"plan","action":"budgetScenario","sourceId":"{{source.SourceId:D}}","year":2026,"month":11,"targets":[{"category":"Food","target":"40.00"}]}
                """)))
        {
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
            var scenario = response.RootElement.GetProperty("data")
                .GetProperty("scenario");
            Assert.Equal(source.Sha256, scenario.GetProperty("sourceSha256").GetString());
            Assert.Equal(30m, scenario.GetProperty("illustrativeReduction").GetDecimal());
            Assert.Equal(360m, scenario.GetProperty("hypotheticalNetAfterPlannedBills").GetDecimal());
        }

        using (var invalid = System.Text.Json.JsonDocument.Parse(
            await protocol.HandleAsync($$"""
                {"requestId":"invalid","action":"budgetScenario","sourceId":"{{source.SourceId:D}}","year":2026,"month":11,"targets":[{"category":"Food","target":"-12.00"}]}
                """)))
        {
            Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean());
        }

        Assert.True(catalog.RevokeSource(source.SourceId));
        using var denied = System.Text.Json.JsonDocument.Parse(
            await protocol.HandleAsync($$"""
                {"requestId":"revoked","action":"budgetScenario","sourceId":"{{source.SourceId:D}}","year":2026,"month":11,"targets":[]}
                """));
        Assert.False(denied.RootElement.GetProperty("ok").GetBoolean());
    }

}
