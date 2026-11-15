import Foundation

struct GraphConceptRegion: Decodable, Equatable, Identifiable {
    let regionId: String
    let label: String
    let containedEntityCount: Int
    let ownerMemberCount: Int
    let acceptedMemberCount: Int
    let verifiedMemberCount: Int

    var id: String { regionId }
}

struct GraphConceptPage: Decodable, Equatable {
    let regionCount: Int
    let regions: [GraphConceptRegion]
    let nextCursor: String?
    let revision: Int64
}
