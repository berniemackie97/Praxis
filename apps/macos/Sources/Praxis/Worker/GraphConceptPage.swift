import Foundation

struct GraphConceptRegion: Decodable, Equatable, Identifiable {
    let regionId: String
    let label: String
    let relationCount: Int
    let relatedEntityCount: Int
    let containedEntityCount: Int

    var id: String { regionId }
}

struct GraphConceptPage: Decodable, Equatable {
    let regionCount: Int
    let regions: [GraphConceptRegion]
    let nextCursor: String?
}
