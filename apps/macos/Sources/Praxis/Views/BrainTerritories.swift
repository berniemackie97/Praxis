import Foundation
import SwiftUI

/// The regions a whole-brain view draws. Top regions are shown, but a region that dominates the
/// brain is opened into its children until about a dozen territories are visible, so a brain that
/// is mostly one project shows that project's themes instead of one undifferentiated blob.
enum BrainTerritories {
    static let target = 12

    static func select(_ map: GraphRegionMap?) -> [GraphKnowledgeRegion] {
        guard let map else { return [] }
        let children = Dictionary(grouping: map.regions.filter { $0.parentId != nil }, by: { $0.parentId! })
        var shown = map.regions.filter { $0.parentId == nil }
        while shown.count < target,
              let largest = shown.filter({ !(children[$0.regionId] ?? []).isEmpty }).max(by: { $0.entities < $1.entities }),
              let index = shown.firstIndex(of: largest) {
            let opened = (children[largest.regionId] ?? []).filter { $0.entities > 0 || $0.pendingSources > 0 }
            guard !opened.isEmpty else { break }
            shown.replaceSubrange(index...index, with: opened)
            if shown.count >= GraphVisualOverview.maximumRenderedRegions { break }
        }
        return Array(shown.sorted { $0.entities == $1.entities ? $0.label < $1.label : $0.entities > $1.entities }
            .prefix(GraphVisualOverview.maximumRenderedRegions))
    }

    /// The top region each region belongs to, for colouring a territory by its family.
    static func family(of region: GraphKnowledgeRegion, in map: GraphRegionMap) -> String {
        var current = region
        let byId = Dictionary(uniqueKeysWithValues: map.regions.map { ($0.regionId, $0) })
        while let parent = current.parentId, let next = byId[parent] { current = next }
        return current.regionId
    }

    /// Links between shown territories: a link between regions is credited to the territories
    /// that contain them, never invented between territories with no evidence.
    static func links(_ map: GraphRegionMap?, shown: [GraphKnowledgeRegion]) -> [GraphVisualLink] {
        guard let map else { return [] }
        let byId = Dictionary(uniqueKeysWithValues: map.regions.map { ($0.regionId, $0) })
        let shownIds = Set(shown.map(\.regionId))
        func territory(_ id: String) -> String? {
            var current: String? = id
            while let value = current {
                if shownIds.contains(value) { return value }
                current = byId[value]?.parentId
            }
            return nil
        }
        var totals: [String: (String, String, Int)] = [:]
        for link in map.links {
            guard let a = territory(link.fromRegionId), let b = territory(link.toRegionId), a != b else { continue }
            let key = a < b ? "\(a)|\(b)" : "\(b)|\(a)"
            let weight = link.relations + link.sharedEntities
            totals[key] = (min(a, b), max(a, b), (totals[key]?.2 ?? 0) + weight)
        }
        return totals.values.sorted { $0.0 == $1.0 ? $0.1 < $1.1 : $0.0 < $1.0 }.map {
            GraphVisualLink(subjectId: $0.0, objectId: $0.1, confirmedCount: $0.2, suggestedCount: 0, contradictedCount: 0)
        }
    }

    static func overview(_ map: GraphRegionMap?) -> GraphVisualOverview {
        let shown = select(map)
        return GraphVisualOverview(regions: shown.map {
            GraphVisualRegion(id: $0.regionId, title: $0.label, memberCount: $0.entities)
        }, links: links(map, shown: shown))
    }
}
