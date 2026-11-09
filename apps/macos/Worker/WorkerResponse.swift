import Foundation

struct WorkerResponseData: Decodable {
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
    let media: MediaInspection?
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

struct WorkerResponse: Decodable {
    let requestId: String?
    let ok: Bool
    let data: WorkerResponseData?
    let error: String?
}
