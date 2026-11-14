import Foundation

/// The private worker's read-only projection of eligible knowledge.
struct GraphEvidence: Decodable, Equatable {
    let proposalId: String
    let sourceId: String
    let itemId: String
    let revision: Int
    let sourceDigest: String
    let locator: String
    let text: String
    let offset: Int
    let status: String
    let verdict: String?
}

extension GraphEvidence {
    func matchesCurrent(_ item: LibraryItem) -> Bool {
        item.sourceId == sourceId && item.itemId == itemId &&
            item.revision == revision && item.sha256 == sourceDigest &&
            item.sourceStatus == "active"
    }

    func excerpt(in content: String) -> String? {
        guard offset >= 0, !text.isEmpty,
              let range = Range(NSRange(location: offset, length: (text as NSString).length), in: content),
              content[range] == text else {
            return nil
        }
        let start = content.index(range.lowerBound, offsetBy: -200, limitedBy: content.startIndex)
            ?? content.startIndex
        let end = content.index(range.upperBound, offsetBy: 400, limitedBy: content.endIndex)
            ?? content.endIndex
        return String(content[start..<end])
    }
}

struct GraphNode: Decodable, Identifiable, Equatable {
    let entityId: String
    let label: String
    let entityType: String?
    let typeOrigin: String
    let status: String
    let mentions: Int
    let sources: Int
    let typeConflict: Bool
    let evidence: GraphEvidence?
    let actions: [String]
    var id: String { entityId }
}

struct GraphEdge: Decodable, Identifiable, Equatable {
    let relationId: String
    let subjectId: String
    let objectId: String
    let predicate: String
    let origin: String
    let evidenceStatus: String
    let evidence: GraphEvidence?
    let actions: [String]
    var id: String { relationId }
}

/// A readable assertion summary retaining every separately reviewable graph observation.
struct GraphRelationGroup: Identifiable {
    let id: String
    let observations: [GraphEdge]

    static func group(_ edges: [GraphEdge]) -> [GraphRelationGroup] {
        Dictionary(grouping: edges) {
            "\($0.subjectId)|\($0.predicate)|\($0.objectId)"
        }
        .map { entry in
            GraphRelationGroup(id: entry.key,
                               observations: entry.value.sorted { $0.relationId < $1.relationId })
        }
        .sorted { $0.id < $1.id }
    }
}

struct GraphPage: Decodable {
    let outcome: String
    let focusId: String?
    let nodes: [GraphNode]
    let edges: [GraphEdge]
    let nextCursor: String?
}

struct GraphPreview: Decodable {
    let outcome: String
    let content: String?
    let reasonCode: String?
}

struct GraphReviewOutcome: Decodable {
    let reviewed: Bool
}

/// Explicit bounds for the visible scene, independent of the catalog's query limits.
enum GraphBudget {
    static let maximumNodes = 120
    static let maximumEdges = 120
    static let seedCount = 32
    static let neighborhoodPageSize = 60
}

/// A page merge never fabricates an edge or substitutes an unverified node.
enum GraphProjection {
    static func merge(nodes: [GraphNode], edges: [GraphEdge], page: GraphPage) -> (nodes: [GraphNode], edges: [GraphEdge]) {
        var knownNodes = Dictionary(uniqueKeysWithValues: nodes.map { ($0.entityId, $0) })
        for node in page.nodes {
            if knownNodes[node.entityId] == nil && knownNodes.count >= GraphBudget.maximumNodes { break }
            knownNodes[node.entityId] = node
        }
        var knownEdges = Dictionary(uniqueKeysWithValues: edges.map { ($0.relationId, $0) })
        for edge in page.edges {
            guard knownEdges.count < GraphBudget.maximumEdges,
                  knownNodes[edge.subjectId] != nil, knownNodes[edge.objectId] != nil else { continue }
            knownEdges[edge.relationId] = edge
        }
        return (
            knownNodes.values.sorted { $0.entityId < $1.entityId },
            knownEdges.values.sorted { $0.relationId < $1.relationId }
        )
    }
}
