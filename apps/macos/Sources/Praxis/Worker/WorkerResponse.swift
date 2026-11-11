import Foundation

struct WorkerResponseData: Decodable {
    let items: [LibraryItem]?
    let status: IntakeStatus?
    let roots: [WatchedRootStatus]?
    let backlog: ProcessingBacklog?
    let proposals: [TopicProposal]?
    let reviewed: Bool?
    let collections: [SourceCollection]?
    let collection: SourceCollection?
    let collectionIds: [String]?
    let moves: [SourceMovement]?
    let changed: Bool?
    let hits: [SearchHit]?
    let mode: String?
    let notice: String?
    let index: SemanticIndexUpdate?
    let clips: [VideoCue]?
    let media: MediaInspection?
    let outcome: String?
    let semanticOutcome: String?
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
