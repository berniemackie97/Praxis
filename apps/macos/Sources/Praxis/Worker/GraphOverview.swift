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

struct GraphZoneOverview: Decodable, Equatable {
    let entityCount: Int
    let relationshipCount: Int
    let zones: [GraphTypeCount]
    let links: [GraphZoneLink]
}

struct GraphZoneLink: Decodable, Equatable {
    let subjectType: String
    let objectType: String
    let confirmedCount: Int
    let suggestedCount: Int
    let contradictedCount: Int

    var count: Int { confirmedCount + suggestedCount + contradictedCount }
}
