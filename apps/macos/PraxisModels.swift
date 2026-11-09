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

struct ResponseData: Decodable {
    let items: [LibraryItem]?
    let proposals: [TopicProposal]?
    let reviewed: Bool?
    let collections: [SourceCollection]?
    let collection: SourceCollection?
    let collectionIds: [String]?
    let changed: Bool?
    let hits: [SearchHit]?
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

