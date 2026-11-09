import Foundation

struct BudgetCategory: Decodable, Identifiable {
    let category: String
    let postedSpending: Double
    let plannedBills: Double
    let rowCount: Int
    var id: String { category }
}

struct BudgetEvidence: Decodable, Identifiable {
    let sourceRow: Int
    let recordId: String
    let date: String
    let kind: String
    let status: String
    let amount: Double
    let category: String
    let description: String
    var id: String { "\(recordId):\(sourceRow)" }
}

struct MonthlyBudgetReport: Decodable {
    let sourceId: String
    let itemId: String
    let revision: Int
    let sourceSha256: String
    let year: Int
    let month: Int
    let currency: String
    let postedIncome: Double
    let postedSpending: Double
    let plannedBills: Double
    let pendingNetSpending: Double
    let actualBalance: Double
    let balanceAfterPlannedBills: Double
    let includedRows: Int
    let ignoredDuplicateRows: Int
    let pendingRows: Int
    let transfersExcluded: Int
    let evidenceTruncated: Bool
    let categories: [BudgetCategory]
    let evidence: [BudgetEvidence]
}

struct BudgetScenarioCategory: Decodable, Identifiable {
    let category: String
    let observedSpending: Double
    let hypotheticalTarget: Double
    let illustrativeReduction: Double
    let plannedBills: Double
    let evidenceRows: Int
    var id: String { category }
}

struct BudgetScenario: Decodable {
    let sourceId: String
    let itemId: String
    let revision: Int
    let sourceSha256: String
    let year: Int
    let month: Int
    let currency: String
    let postedIncome: Double
    let postedSpending: Double
    let plannedBills: Double
    let baselineNetAfterPlannedBills: Double
    let illustrativeReduction: Double
    let hypotheticalNetAfterPlannedBills: Double
    let pendingRows: Int
    let evidenceTruncated: Bool
    let categories: [BudgetScenarioCategory]
}
