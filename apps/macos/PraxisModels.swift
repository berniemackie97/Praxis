import Foundation

struct LibraryItem: Decodable, Identifiable {
    let sourceId: String
    let itemId: String
    let sourceKind: String
    let sourceLocator: String
    let sourceStatus: String
    let revision: Int
    let sha256: String
    let originalBytes: Int64
    let processingState: String?
    let extractionOutcome: String?
    var id: String { itemId }
    var title: String { URL(fileURLWithPath: sourceLocator).lastPathComponent }
}

struct SearchHit: Decodable, Identifiable {
    let sourceId: String
    let itemId: String
    let revision: Int
    let title: String
    let sourceLocator: String
    let originalSha256: String
    let snippet: String
    var id: String { "\(itemId)-\(revision)" }
}

struct SourceCollection: Decodable, Identifiable {
    let collectionId: String
    let name: String
    let activeSourceCount: Int
    var id: String { collectionId }
}

struct TopicProposal: Decodable, Identifiable {
    let proposalId: String
    let sourceId: String
    let itemId: String
    let revision: Int
    let sourceSha256: String
    let producer: String
    let value: String
    let evidence: String
    let evidenceOffset: Int
    let status: String
    let correction: String?
    var id: String { proposalId }
}

struct OriginalExportReceipt: Decodable {
    let itemId: String
    let revision: Int
    let sha256: String
    let length: Int64
}

struct IntakeFileError: Decodable, Identifiable {
    let sourcePath: String
    let message: String
    var id: String { sourcePath }
}

struct IntakeStatus: Decodable {
    let state: String
    let paused: Bool
    let lastSuccessfulScanUtc: String?
    let observedFiles: Int
    let importedSinceStart: Int
    let errorCount: Int
    let fileErrors: [IntakeFileError]
    let problem: String?
}

struct SourceMovement: Decodable, Identifiable {
    let sourceId: String
    let previousPath: String
    let currentPath: String
    let sha256: String
    let recordedUtc: String
    var id: String { "\(sourceId):\(recordedUtc):\(previousPath)" }
}


struct VideoCue: Decodable, Identifiable {
    let captionSourceId: String
    let captionItemId: String
    let captionRevision: Int
    let captionSha256: String
    let captionOffset: Int
    let captionLength: Int
    let cueNumber: Int
    let startMilliseconds: Int64
    let endMilliseconds: Int64
    let captionText: String
    let producer: String
    let videoSourceId: String
    let videoItemId: String
    let videoRevision: Int
    let videoSha256: String
    let videoLocator: String
    var id: String { "\(captionItemId):\(captionRevision):\(cueNumber)" }
    var range: String {
        "\(startMilliseconds / 1000)s–\(endMilliseconds / 1000)s"
    }
}

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
    var id: String { recordId }
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

struct ResponseData: Decodable {
    let items: [LibraryItem]?
    let status: IntakeStatus?
    let proposals: [TopicProposal]?
    let reviewed: Bool?
    let collections: [SourceCollection]?
    let collection: SourceCollection?
    let collectionIds: [String]?
    let moves: [SourceMovement]?
    let changed: Bool?
    let hits: [SearchHit]?
    let clips: [VideoCue]?
    let budget: MonthlyBudgetReport?
    let scenario: BudgetScenario?
    let outcome: String?
    let content: String?
    let reasonCode: String?
    let verified: Bool?
    let exported: OriginalExportReceipt?
    let queued: Bool?
    let revoked: Bool?
}

struct Response: Decodable {
    let requestId: String?
    let ok: Bool
    let data: ResponseData?
    let error: String?
}

