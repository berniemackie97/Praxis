namespace Praxis.Storage;

public sealed record MonthlyBudgetTarget(string Category, decimal TargetSpending);

public sealed record MonthlyBudgetScenarioCategory(
    string Category, decimal ObservedSpending, decimal HypotheticalTarget,
    decimal IllustrativeReduction, decimal PlannedBills, int EvidenceRows);

public sealed record MonthlyBudgetScenario(
    Guid SourceId, Guid ItemId, int Revision, string SourceSha256,
    int Year, int Month, string Currency,
    decimal PostedIncome, decimal PostedSpending, decimal PlannedBills,
    decimal BaselineNetAfterPlannedBills, decimal IllustrativeReduction,
    decimal HypotheticalNetAfterPlannedBills, int PendingRows,
    bool EvidenceTruncated,
    IReadOnlyList<MonthlyBudgetScenarioCategory> Categories);

public sealed partial class LocalFinancialEvidence
{
    public async Task<MonthlyBudgetScenario> PreviewAsync(
        Guid sourceId, int year, int month,
        IReadOnlyList<MonthlyBudgetTarget> targets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(targets));
        }

        var report = await AnalyzeAsync(sourceId, year, month, cancellationToken);
        var categories = report.Categories.ToDictionary(x => x.Category, StringComparer.Ordinal);
        var submitted = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(target.Category) ||
                !categories.TryGetValue(target.Category, out var category) ||
                category.PostedSpending <= 0m ||
                target.TargetSpending < 0m ||
                decimal.Round(target.TargetSpending, 2) != target.TargetSpending ||
                target.TargetSpending > category.PostedSpending ||
                !submitted.TryAdd(target.Category, target.TargetSpending))
            {
                throw new ArgumentException(
                    "Targets must uniquely identify categories with positive posted spending " +
                    "and a target between zero and the observed amount.", nameof(targets));
            }
        }

        var lines = new List<MonthlyBudgetScenarioCategory>(categories.Count);
        var reduction = 0m;
        foreach (var category in report.Categories)
        {
            var target = submitted.TryGetValue(category.Category, out var selected)
                ? selected : category.PostedSpending;
            var difference = category.PostedSpending - target;
            reduction = checked(reduction + difference);
            lines.Add(new MonthlyBudgetScenarioCategory(
                category.Category, category.PostedSpending, target, difference,
                category.PlannedBills, category.RowCount));
        }

        AssertBudgetSourceCurrent(
            report.SourceId, report.ItemId, report.Revision, report.SourceSha256);

        return new MonthlyBudgetScenario(
            report.SourceId, report.ItemId, report.Revision, report.SourceSha256,
            report.Year, report.Month, report.Currency, report.PostedIncome,
            report.PostedSpending, report.PlannedBills, report.BalanceAfterPlannedBills,
            reduction, checked(report.BalanceAfterPlannedBills + reduction),
            report.PendingRows, report.EvidenceTruncated, lines.AsReadOnly());
    }
}
