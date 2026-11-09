import SwiftUI

struct BudgetView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @ObservedObject var budget: BudgetWorkbenchModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Household budget").font(.title2.weight(.semibold))
            Text("Read-only analysis of an approved local financial export. This CSV profile is one offline input adapter, not a bank connection or a complete household ledger.")
                .foregroundStyle(.secondary)
            if let source = model.selected, budget.hasActiveSource {
                Text("Selected: \(source.title) · revision \(source.revision)")
                    .font(.caption).textSelection(.enabled)
            } else {
                Text("Select an active financial CSV in Inbox & Library first.")
                    .font(.caption).foregroundStyle(.secondary)
            }

            HStack {
                Picker("Month", selection: Binding(
                    get: { budget.month },
                    set: { budget.changeMonth($0) }
                )) {
                    ForEach(1...12, id: \.self) { month in
                        Text("\(month)").tag(month)
                    }
                }
                .frame(width: 125)
                Stepper("Year: \(budget.year)", value: Binding(
                    get: { budget.year },
                    set: { budget.changeYear($0) }
                ), in: 2000...2100)
                .frame(width: 190)
                Button(budget.analyzing ? "Analyzing…" : "Analyze selected CSV") {
                    budget.analyze()
                }
                .disabled(!budget.hasActiveSource || budget.analyzing)
            }

            if let error = budget.error {
                Text(error).foregroundStyle(.red).font(.caption).textSelection(.enabled)
            }
            if let report = budget.report {
                ScrollView {
                    VStack(alignment: .leading, spacing: 12) {
                        summary(report)
                        categories(report)
                        targets(report)
                        evidence(report)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            } else {
                Spacer(minLength: 0)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func summary(_ report: MonthlyBudgetReport) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("USD · \(report.year)-\(String(format: "%02d", report.month)) · Source SHA-256 \(report.sourceSha256)")
                .font(.caption).textSelection(.enabled)
            Grid(alignment: .leading, horizontalSpacing: 20, verticalSpacing: 8) {
                GridRow {
                    Text("Posted income")
                    Text(report.postedIncome, format: .currency(code: report.currency))
                }
                GridRow {
                    Text("Posted spending")
                    Text(report.postedSpending, format: .currency(code: report.currency))
                }
                GridRow {
                    Text("Unpaid planned bills")
                    Text(report.plannedBills, format: .currency(code: report.currency))
                }
                GridRow {
                    Text("Net after unpaid planned bills (not an account balance)")
                    Text(report.balanceAfterPlannedBills, format: .currency(code: report.currency))
                        .fontWeight(.semibold)
                }
            }
            Text("\(report.pendingRows) pending items (net \(report.pendingNetSpending.formatted(.currency(code: report.currency)))); \(report.transfersExcluded) transfers excluded; \(report.ignoredDuplicateRows) exact duplicate rows skipped.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private func categories(_ report: MonthlyBudgetReport) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Categories").font(.headline)
            ForEach(report.categories) { category in
                HStack {
                    Text(category.category)
                    Spacer()
                    Text(category.postedSpending, format: .currency(code: report.currency))
                    Text(" + planned ").foregroundStyle(.secondary)
                    Text(category.plannedBills, format: .currency(code: report.currency))
                }
            }
        }
    }

    private func targets(_ report: MonthlyBudgetReport) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Divider()
            Text("Try your own spending targets").font(.headline)
            Text("Targets describe a hypothetical comparable month, not savings already achieved. Praxis does not classify which costs are optional.")
                .font(.caption).foregroundStyle(.secondary)
            ForEach(Array(report.categories.filter { $0.postedSpending > 0 }.prefix(50))) { category in
                HStack {
                    Text(category.category)
                    Spacer()
                    Text("Observed \(category.postedSpending.formatted(.currency(code: report.currency)))")
                        .font(.caption)
                    TextField("Target USD", text: Binding(
                        get: { budget.targetText[category.category] ?? "" },
                        set: { budget.editTarget(category: category.category, value: $0) }
                    ))
                    .frame(width: 110)
                    .textFieldStyle(.roundedBorder)
                    .accessibilityLabel("Spending target for \(category.category)")
                }
            }
            if report.categories.filter({ $0.postedSpending > 0 }).count > 50 {
                Text("Only the first 50 positive-spending categories can be compared.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Button(budget.previewing ? "Previewing…" : "Preview budget scenario") {
                budget.preview()
            }
            .disabled(budget.previewing || report.categories.allSatisfy { $0.postedSpending <= 0 })
            if let scenario = budget.scenario,
               scenario.sourceSha256 == report.sourceSha256,
               scenario.itemId == report.itemId,
               scenario.revision == report.revision {
                Text("Illustrative scenario").font(.headline)
                Text("Observed net after unpaid bills: \(scenario.baselineNetAfterPlannedBills.formatted(.currency(code: scenario.currency)))")
                Text("Hypothetical reduction: \(scenario.illustrativeReduction.formatted(.currency(code: scenario.currency)))")
                Text("Hypothetical net after unpaid bills: \(scenario.hypotheticalNetAfterPlannedBills.formatted(.currency(code: scenario.currency)))")
                    .fontWeight(.semibold)
                Text("Actual transactions, pending items, unpaid bills and source evidence remain unchanged. Reaching a target is not guaranteed.")
                    .font(.caption).foregroundStyle(.secondary)
                ForEach(scenario.categories.filter { $0.illustrativeReduction > 0 }) { entry in
                    Text("\(entry.category): \(entry.observedSpending.formatted(.currency(code: scenario.currency))) → \(entry.hypotheticalTarget.formatted(.currency(code: scenario.currency)))")
                        .font(.caption)
                }
            }
        }
    }

    private func evidence(_ report: MonthlyBudgetReport) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Divider()
            Text("Source-row evidence").font(.headline)
            ForEach(report.evidence) { row in
                Text("Row \(row.sourceRow) · \(row.date) · \(row.kind) · \(row.description) · \(row.amount.formatted(.currency(code: report.currency)))")
                    .font(.caption).textSelection(.enabled)
            }
            if report.evidenceTruncated {
                Text("Only the first 100 relevant rows are shown; export the original for complete evidence.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }
}
