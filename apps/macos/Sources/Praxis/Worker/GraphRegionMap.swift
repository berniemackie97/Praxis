import Foundation

/// One brain region derived from the owner's folders and repositories (see the worker's
/// `graphKnowledgeRegions`). `kind` is group, project, folder or loose.
struct GraphKnowledgeRegion: Decodable, Identifiable, Equatable {
    let regionId: String
    let parentId: String?
    let label: String
    let kind: String
    let summary: String?
    let entities: Int
    let sources: Int
    let pendingSources: Int
    let relations: Int
    let hubs: [GraphNode]
    var id: String { regionId }

    /// Captured sources are still being understood and nothing is known from them yet.
    var forming: Bool { entities == 0 && pendingSources > 0 }
}

/// Confirmed evidence between two regions: cross-region relations and shared entities.
struct GraphRegionLink: Decodable, Equatable {
    let fromRegionId: String
    let toRegionId: String
    let relations: Int
    let sharedEntities: Int
}

/// The whole brain at one knowledge revision.
struct GraphRegionMap: Decodable, Equatable {
    let revision: Int64
    let entities: Int
    let unplacedEntities: Int
    let regions: [GraphKnowledgeRegion]
    let links: [GraphRegionLink]
}
