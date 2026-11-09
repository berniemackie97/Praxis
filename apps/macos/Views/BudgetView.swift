import SwiftUI

struct BudgetView: View {
    @EnvironmentObject private var model: WorkbenchModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Monthly budget evidence").font(.title2.weight(.semibold))
            Text("Read-only local analysis of one selected owner-imported CSV. This is not yet an AI budget recommendation or a live account connection.")
                .font(.caption).foregroundStyle(.secondary)
            if let source = model.selected,
               source.sourceStatus == "active",
               source.isBudgetSource {
                Text("Source: \(source.title) · Revision \(source.revision)")
                    .font(.caption).textSelection(.enabled)
            } else {
                Text("Select an active .csv original in Inbox & Library, then return here.")
                    .foregroundStyle(.secondary)
            }

            HStack {
                Picker("Month", selection: $model.budgetMonth) {
                    ForEach(1...12, id: \.self) { month in
                        Text("\(month)").tag(month)
                    }
                }.frame(width: 120)
                Stepper("Year: \(model.budgetYear)", value: $model.budgetYear, in: 2000...2100)
                    .frame(width: 180)
                Button("Analyze selected CSV") { model.analyzeSelectedBudget() }
                    .disabled(model.selected?.sourceStatus != "active" ||
                        model.selected?.isBudgetSource != true)
            }

            if let report = model.budgetReport {
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
                Text("Categories").font(.headline)
                ScrollView {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(report.categories) { category in
                            HStack {
                                Text(category.category)
                                Spacer()
                                Text(category.postedSpending, format: .currency(code: report.currency))
                                Text(" + planned ")
                                    .foregroundStyle(.secondary)
                                Text(category.plannedBills, format: .currency(code: report.currency))
                            }
                        }
                        Divider()
                        Text("Try your own spending targets").font(.headline)
                        Text("These targets compare with spending already posted. They describe a hypothetical comparable month, not money already saved. Praxis does not classify expenses as optional.")
                            .font(.caption).foregroundStyle(.secondary)
                        ForEach(Array(report.categories.filter {
                            $0.postedSpending > 0
                        }.prefix(50))) { category in
                            HStack {
                                Text(category.category)
                                Spacer()
                                Text("Observed: \(category.postedSpending.formatted(.currency(code: report.currency)))")
                                    .font(.caption)
                                TextField("Target USD", text: Binding(
                                    get: { model.budgetTargetText[category.category] ?? "" },
                                    set: { model.updateBudgetTarget(category: category.category, text: $0) }
                                ))
                                .frame(width: 110)
                                .textFieldStyle(.roundedBorder)
                                .accessibilityLabel("Spending target for \(category.category)")
                            }
                        }
                        if report.categories.filter({ $0.postedSpending > 0 }).count > 50 {
                            Text("Only the first 50 positive-spending categories can be edited in this preview.")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Button("Preview budget scenario") {
                            model.previewBudgetScenario()
                        }
                        .disabled(report.categories.allSatisfy { $0.postedSpending <= 0 })
                        if let scenario = model.budgetScenario,
                           scenario.itemId == report.itemId,
                           scenario.revision == report.revision,
                           scenario.sourceSha256 == report.sourceSha256 {
                            Text("Illustrative scenario").font(.headline)
                            Text("Observed net after unpaid bills: \(scenario.baselineNetAfterPlannedBills.formatted(.currency(code: scenario.currency)))")
                            Text("Hypothetical reduction: \(scenario.illustrativeReduction.formatted(.currency(code: scenario.currency)))")
                            Text("Hypothetical net after unpaid bills: \(scenario.hypotheticalNetAfterPlannedBills.formatted(.currency(code: scenario.currency)))")
                                .fontWeight(.semibold)
                            Text("Actual transactions, pending items, unpaid bills and source evidence are unchanged. Reaching a target is not guaranteed.")
                                .font(.caption).foregroundStyle(.secondary)
                            ForEach(scenario.categories.filter {
                                $0.illustrativeReduction > 0
                            }) { line in
                                Text("\(line.category): \(line.observedSpending.formatted(.currency(code: scenario.currency))) → \(line.hypotheticalTarget.formatted(.currency(code: scenario.currency)))")
                                    .font(.caption)
                            }
                        }
                        Divider()
                        Text("Source-row evidence").font(.headline)
                        ForEach(report.evidence) { entry in
                            Text("Row \(entry.sourceRow) · \(entry.date) · \(entry.kind) · \(entry.description) · \(entry.amount.formatted(.currency(code: report.currency)))")
                                .font(.caption).textSelection(.enabled)
                        }
                        if report.evidenceTruncated {
                            Text("Only the first 100 matching rows are shown; export the original for complete evidence.")
                                .foregroundStyle(.secondary)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
            Spacer(minLength: 0)
        }
    }
}
