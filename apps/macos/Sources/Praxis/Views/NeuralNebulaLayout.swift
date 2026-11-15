import Foundation
import simd

struct NeuralNebulaPlacement {
    let zoneCenters: [String: SIMD3<Float>]
    let zoneSizes: [String: Float]
    let nodeCenters: [String: SIMD3<Float>]
}

enum NeuralNebulaLayout {
    static func place(zones overview: GraphZoneOverview, nodes: [GraphNode],
                      edges: [GraphEdge], regionType: String?) -> NeuralNebulaPlacement {
        let ordered = overview.zones.filter { $0.count > 0 }.sorted {
            $0.count == $1.count ? $0.entityType < $1.entityType : $0.count > $1.count
        }
        let goldenAngle = Float.pi * (3 - sqrt(Float(5)))
        let centers = Dictionary(uniqueKeysWithValues: ordered.enumerated().map { index, zone in
            let fraction = (Float(index) + 0.5) / Float(ordered.count)
            let latitude = 1 - 2 * fraction
            let radial = sqrt(max(0, 1 - latitude * latitude))
            let angle = Float(index) * goldenAngle
            let position = ordered.count == 1 ? SIMD3<Float>.zero :
                SIMD3<Float>(cos(angle) * radial, latitude, sin(angle) * radial) * 185
            return (zone.entityType, position)
        })
        let sizes = Dictionary(uniqueKeysWithValues: ordered.map {
            ($0.entityType, min(29, 9 + log2(Float($0.count) + 1) * 2.2))
        })
        guard let regionType, let center = centers[regionType], !nodes.isEmpty else {
            return NeuralNebulaPlacement(zoneCenters: centers, zoneSizes: sizes, nodeCenters: [:])
        }

        let flat = GraphAtlasLayout.positions(nodes: nodes, edges: edges)
        let extent = max(1, flat.values.reduce(CGFloat(0)) { maximum, point in
            max(maximum, hypot(point.x, point.y))
        })
        let radius = min(Float(85), 20 + sqrt(Float(nodes.count)) * 2.6)
        let positions = Dictionary(uniqueKeysWithValues: nodes.compactMap { node -> (String, SIMD3<Float>)? in
            guard let point = flat[node.entityId] else { return nil }
            let x = Float(point.x / extent) * radius
            let y = Float(point.y / extent) * radius
            let depth = sqrt(max(0, radius * radius - x * x - y * y))
            let sign: Float = stableHash(node.entityId) & 1 == 0 ? 1 : -1
            return (node.entityId, center + SIMD3<Float>(x, y, depth * sign * 0.65))
        })
        return NeuralNebulaPlacement(zoneCenters: centers, zoneSizes: sizes, nodeCenters: positions)
    }

    static func color(for type: String) -> SIMD4<Float> {
        let palette: [SIMD4<Float>] = [
            SIMD4<Float>(0.28, 0.77, 1.00, 1),
            SIMD4<Float>(0.65, 0.57, 1.00, 1),
            SIMD4<Float>(0.35, 0.92, 0.79, 1),
            SIMD4<Float>(1.00, 0.68, 0.50, 1),
            SIMD4<Float>(0.91, 0.54, 0.87, 1)
        ]
        return palette[Int(stableHash(type) % UInt64(palette.count))]
    }

    private static func stableHash(_ value: String) -> UInt64 {
        value.utf8.reduce(14_695_981_039_346_656_037) { ($0 ^ UInt64($1)) &* 1_099_511_628_211 }
    }
}
