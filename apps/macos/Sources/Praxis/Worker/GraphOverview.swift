import Foundation

/// Scoped counts from the canonical catalog; not an all-nodes graph or a separate knowledge store.
struct GraphOverview: Decodable, Equatable {
    let entityCount: Int
    let types: [GraphTypeCount]
}

struct GraphTypeCount: Decodable, Equatable, Identifiable {
    let entityType: String
    let count: Int
    var id: String { entityType }
}
