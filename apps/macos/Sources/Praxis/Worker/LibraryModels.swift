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
    var isVideo: Bool { Self.videoExtensions.contains(fileExtension) }

    private static let videoExtensions: Set<String> = ["mp4", "mov", "m4v", "webm", "mkv"]
    private var fileExtension: String {
        URL(fileURLWithPath: sourceLocator).pathExtension.lowercased()
    }
}

struct SemanticIndexUpdate: Decodable {
    let indexed: Int
    let hasMore: Bool
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

/// A source-grounded proposal: a topic, a typed entity, or a relation between two entities.
struct ReviewedCount: Decodable { let reviewed: Int }

struct TopicProposal: Decodable, Identifiable {
    let proposalId: String
    let sourceId: String
    let itemId: String
    let revision: Int
    let sourceSha256: String
    let producer: String
    let kind: String
    let entityType: String?
    let value: String
    let evidence: String
    let evidenceOffset: Int
    let status: String
    let correction: String?
    let relationSubject: String?
    let relationObject: String?
    let flags: String?
    /// Counts as knowledge: accepted by the owner, supported by a verifier, or verified intake output.
    let confirmed: Bool?
    var isConfirmed: Bool { confirmed == true }
    var id: String { proposalId }

    var instructionLike: Bool { flags == "instruction_like" }
    var fromModel: Bool { producer.hasPrefix("semantic-extract") }

    var title: String {
        switch kind {
        case "relation": "\(relationSubject ?? "") \(KnowledgeVocabulary.label(value)) \(relationObject ?? "")"
        case "entity": "\(value) · \(KnowledgeVocabulary.label(entityType))"
        default: value
        }
    }
}

struct OriginalExportReceipt: Decodable {
    let itemId: String
    let revision: Int
    let sha256: String
    let length: Int64
}
