import CoreGraphics
import Foundation

/// Stable positions derived only from the returned graph components, never from inferred links.
enum GraphAtlasLayout {
    static func positions(nodes: [GraphNode], edges: [GraphEdge]) -> [String: CGPoint] {
        let ids = Set(nodes.map(\.entityId))
        var adjacent: [String: Set<String>] = [:]
        for edge in edges where ids.contains(edge.subjectId) && ids.contains(edge.objectId) {
            adjacent[edge.subjectId, default: []].insert(edge.objectId)
            adjacent[edge.objectId, default: []].insert(edge.subjectId)
        }

        var remaining = ids
        var components: [[String]] = []
        while let start = remaining.min() {
            var queue = [start]
            remaining.remove(start)
            var index = 0
            while index < queue.count {
                let current = queue[index]
                index += 1
                for neighbor in (adjacent[current] ?? []).sorted() where remaining.remove(neighbor) != nil {
                    queue.append(neighbor)
                }
            }
            components.append(queue)
        }
        components.sort {
            $0.count == $1.count ? ($0.min() ?? "") < ($1.min() ?? "") : $0.count > $1.count
        }

        let goldenAngle = CGFloat.pi * (3 - sqrt(CGFloat(5)))
        var packedArea: CGFloat = 0
        var result: [String: CGPoint] = [:]
        for (index, component) in components.enumerated() {
            let groupRadius = max(12, sqrt(CGFloat(component.count)) * 14)
            let centerRadius = sqrt(packedArea / .pi) + (index == 0 ? 0 : groupRadius + 8)
            let placementAngle = CGFloat(index) * goldenAngle
            let center = CGPoint(x: cos(placementAngle) * centerRadius,
                                 y: sin(placementAngle) * centerRadius)
            packedArea += .pi * pow(groupRadius + 10, 2)

            let hub = component.sorted {
                let left = adjacent[$0]?.count ?? 0
                let right = adjacent[$1]?.count ?? 0
                return left == right ? $0 < $1 : left > right
            }[0]
            result[hub] = center
            var depths = [hub: 0]
            var queue = [hub]
            var cursor = 0
            while cursor < queue.count {
                let current = queue[cursor]
                cursor += 1
                for neighbor in (adjacent[current] ?? []).sorted() where depths[neighbor] == nil {
                    depths[neighbor] = (depths[current] ?? 0) + 1
                    queue.append(neighbor)
                }
            }

            for (order, id) in component.sorted().enumerated() where id != hub {
                let depth = CGFloat(depths[id] ?? 1)
                let angle = CGFloat(order + 1) * goldenAngle
                let radius = min(groupRadius, depth * 18 + sqrt(CGFloat(order + 1)) * 6)
                result[id] = CGPoint(x: center.x + cos(angle) * radius,
                                     y: center.y + sin(angle) * radius)
            }
        }
        return result
    }
}
