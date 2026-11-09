import Combine
import Foundation

@MainActor
final class BudgetWorkbenchModel: ObservableObject {
    @Published private(set) var report: MonthlyBudgetReport?
    @Published private(set) var scenario: BudgetScenario?
    @Published private(set) var error: String?
    @Published private(set) var analyzing = false
    @Published private(set) var previewing = false
    @Published var targetText: [String: String] = [:]
    @Published private(set) var month = Calendar.current.component(.month, from: Date())
    @Published private(set) var year = Calendar.current.component(.year, from: Date())

    private let worker: WorkerSession
    private var selected: LibraryItem?
    private var revisionToken = UUID()
    private var scenarioToken = UUID()

    init(worker: WorkerSession) {
        self.worker = worker
    }

    var hasActiveSource: Bool {
        guard let selected else { return false }
        return selected.sourceStatus == "active" &&
            URL(fileURLWithPath: selected.sourceLocator).pathExtension.lowercased() == "csv"
    }

    func select(_ item: LibraryItem?) {
        if selected?.sourceId == item?.sourceId &&
            selected?.itemId == item?.itemId &&
            selected?.revision == item?.revision &&
            selected?.sha256 == item?.sha256 &&
            selected?.sourceStatus == item?.sourceStatus {
            return
        }

        selected = item
        invalidate()
    }

    func changeMonth(_ value: Int) {
        guard month != value else { return }
        month = value
        invalidate()
    }

    func changeYear(_ value: Int) {
        guard year != value else { return }
        year = value
        invalidate()
    }

    func editTarget(category: String, value: String) {
        targetText[category] = value
        scenarioToken = UUID()
        scenario = nil
    }

    func analyze() {
        guard !analyzing else { return }
        guard let source = selected, hasActiveSource else {
            error = "Select an active financial CSV in the Library first."
            return
        }

        invalidate()
        let token = revisionToken
        analyzing = true
        do {
            try worker.send("budgetSummary", fields: [
                "sourceId": source.sourceId, "year": year, "month": month
            ]) { [weak self] response in
                guard let self else { return }
                guard self.revisionToken == token else { return }
                self.analyzing = false
                guard response.ok, let result = response.data?.budget else {
                    self.error = response.error ?? "Budget analysis is unavailable."
                    return
                }

                guard self.matches(result.sourceId, result.itemId,
                                   result.revision, result.sourceSha256,
                                   result.year, result.month) else {
                    self.error = "The financial source changed. Refresh the Library and retry."
                    return
                }

                self.report = result
            }
        } catch {
            analyzing = false
            self.error = error.localizedDescription
        }
    }

    func preview() {
        guard let report, !previewing, hasActiveSource else { return }

        let targets = targetText
            .filter { !$0.value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .map { ["category": $0.key, "target": $0.value] }
        scenario = nil
        error = nil
        scenarioToken = UUID()
        let token = scenarioToken
        let version = revisionToken
        previewing = true
        do {
            try worker.send("budgetScenario", fields: [
                "sourceId": report.sourceId, "year": report.year, "month": report.month,
                "targets": targets
            ]) { [weak self] response in
                guard let self else { return }
                guard self.revisionToken == version, self.scenarioToken == token else { return }
                self.previewing = false
                guard response.ok, let result = response.data?.scenario else {
                    self.error = response.error ?? "Budget scenario is unavailable."
                    return
                }

                guard self.matches(result.sourceId, result.itemId,
                                   result.revision, result.sourceSha256,
                                   result.year, result.month),
                      result.sourceSha256 == self.report?.sourceSha256 else {
                    self.error = "The financial source changed. Reanalyze before previewing."
                    return
                }

                self.scenario = result
            }
        } catch {
            previewing = false
            self.error = error.localizedDescription
        }
    }

    func reset() {
        selected = nil
        invalidate()
    }

    private func invalidate() {
        revisionToken = UUID()
        scenarioToken = UUID()
        report = nil
        scenario = nil
        targetText.removeAll()
        error = nil
        analyzing = false
        previewing = false
    }

    private func matches(
        _ sourceId: String, _ itemId: String, _ revision: Int,
        _ digest: String, _ reportYear: Int, _ reportMonth: Int
    ) -> Bool {
        guard let selected else { return false }
        return selected.sourceStatus == "active" &&
            selected.sourceId == sourceId &&
            selected.itemId == itemId &&
            selected.revision == revision &&
            selected.sha256 == digest &&
            year == reportYear && month == reportMonth
    }
}
