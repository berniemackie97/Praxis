import Foundation

enum GraphRegionIdentity {
    static func hash(_ value: String) -> UInt64 {
        value.utf8.reduce(14_695_981_039_346_656_037) {
            ($0 ^ UInt64($1)) &* 1_099_511_628_211
        }
    }
}

struct GraphVisualRegion: Equatable, Identifiable {
    let id: String
    let title: String
    let memberCount: Int
    let relatedCount: Int

    var count: Int { memberCount + 1 }
}

struct GraphVisualLink: Equatable {
    let subjectId: String
    let objectId: String
    let confirmedCount: Int
    let suggestedCount: Int
    let contradictedCount: Int

    var count: Int { confirmedCount + suggestedCount + contradictedCount }
}

struct GraphVisualOverview: Equatable {
    static let maximumRenderedRegions = 64

    let regions: [GraphVisualRegion]
    let links: [GraphVisualLink]

    init(regions: [GraphVisualRegion], links: [GraphVisualLink] = []) {
        self.regions = regions
        self.links = links
    }

    init(confirmedProjects: [GraphConceptRegion]) {
        regions = confirmedProjects.prefix(Self.maximumRenderedRegions).map { project in
            GraphVisualRegion(id: project.regionId, title: project.label,
                              memberCount: project.containedEntityCount,
                              relatedCount: project.relatedEntityCount)
        }
        // Cross-project lines require an actual scoped region-link read; no links are inferred.
        links = []
    }
}
