import Foundation
import simd

struct NeuralNebulaPlacement {
    let zoneCenters: [String: SIMD3<Float>]
    let zoneSizes: [String: Float]
    let nodeCenters: [String: SIMD3<Float>]
}

enum NeuralNebulaLayout {
    static func place(zones overview: GraphVisualOverview, nodes: [GraphNode],
                      edges: [GraphEdge], regionId: String?) -> NeuralNebulaPlacement {
        let ordered = overview.regions.filter { $0.count > 0 }.sorted {
            $0.count == $1.count ? $0.id < $1.id : $0.count > $1.count
        }
        let centers = universe(ordered, links: overview.links)
        let sizes = Dictionary(uniqueKeysWithValues: ordered.map {
            ($0.id, min(34, 8 + log2(Float($0.count) + 1) * 2.6))
        })
        guard !nodes.isEmpty else {
            return NeuralNebulaPlacement(zoneCenters: centers, zoneSizes: sizes, nodeCenters: [:])
        }
        let center = regionId.flatMap { centers[$0] } ?? .zero

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
            let sign: Float = GraphRegionIdentity.hash(node.entityId) & 1 == 0 ? 1 : -1
            return (node.entityId, center + SIMD3<Float>(x, y, depth * sign * 0.65))
        })
        return NeuralNebulaPlacement(zoneCenters: centers, zoneSizes: sizes, nodeCenters: positions)
    }

    /// Galaxies placed by evidence: a short deterministic force layout in a flattened disc, where
    /// confirmed cross-region evidence pulls galaxies together and everything else repels. Distance
    /// therefore encodes evidence, never similarity or guesswork.
    static func universe(_ zones: [GraphVisualRegion], links: [GraphVisualLink]) -> [String: SIMD3<Float>] {
        guard zones.count > 1 else { return Dictionary(uniqueKeysWithValues: zones.map { ($0.id, SIMD3<Float>.zero) }) }
        let goldenAngle = Float.pi * (3 - sqrt(Float(5)))
        var positions = zones.enumerated().map { index, _ -> SIMD3<Float> in
            let fraction = (Float(index) + 0.5) / Float(zones.count)
            let radius = 60 + 150 * sqrt(fraction)
            let angle = Float(index) * goldenAngle
            return SIMD3<Float>(cos(angle) * radius, sin(angle) * radius, (fraction - 0.5) * 30)
        }
        let index = Dictionary(uniqueKeysWithValues: zones.enumerated().map { ($0.element.id, $0.offset) })
        let springs = links.compactMap { link -> (Int, Int, Float)? in
            guard let a = index[link.subjectId], let b = index[link.objectId], a != b else { return nil }
            return (a, b, log2(Float(link.count) + 1))
        }
        for iteration in 0..<220 {
            let cooling = 1 - Float(iteration) / 220
            var forces = Array(repeating: SIMD3<Float>.zero, count: zones.count)
            for a in 0..<zones.count {
                for b in (a + 1)..<zones.count {
                    var delta = positions[a] - positions[b]
                    let distance = max(8, simd_length(delta))
                    delta /= distance
                    let push = 30_000 / (distance * distance)
                    forces[a] += delta * push
                    forces[b] -= delta * push
                }
                forces[a] -= positions[a] * 0.006
            }
            for (a, b, weight) in springs {
                let delta = positions[b] - positions[a]
                let pull = delta * 0.012 * weight
                forces[a] += pull
                forces[b] -= pull
            }
            for item in 0..<zones.count {
                let step = forces[item]
                let length = simd_length(step)
                positions[item] += length > 6 ? step / length * 6 * cooling : step * cooling
            }
        }
        // Face-on universe of fixed extent, thin in depth, so it reads like a sky map at any size.
        // Radial distance is compressed (r^0.55) so a few unconnected islands cannot squeeze the
        // connected cluster into a knot: order and relative distance survive, overlap does not.
        let mean = positions.reduce(SIMD3<Float>.zero, +) / Float(positions.count)
        let extent = max(1, positions.map { simd_length(SIMD2<Float>($0.x - mean.x, $0.y - mean.y)) }.max() ?? 1)
        positions = positions.map { position in
            let planar = SIMD2<Float>(position.x - mean.x, position.y - mean.y)
            let radius = simd_length(planar)
            let scaled = radius > 0 ? planar / radius * pow(radius / extent, 0.55) : planar
            return SIMD3<Float>(scaled.x * 230, scaled.y * 160, (position.z - mean.z) * 0.4)
        }
        return Dictionary(uniqueKeysWithValues: zones.enumerated().map { ($0.element.id, positions[$0.offset]) })
    }

    /// The colour a region has in every brain style (by its place in the drawn territories).
    static func regionColor(_ index: Int, forming: Bool) -> SIMD4<Float> {
        let rgb = forming ? BrainPalette.forming : BrainPalette.hues[index % BrainPalette.hues.count]
        return SIMD4<Float>(rgb, 1)
    }

    static func color(for regionId: String) -> SIMD4<Float> {
        let palette: [SIMD4<Float>] = [
            SIMD4<Float>(0.28, 0.77, 1.00, 1),
            SIMD4<Float>(0.65, 0.57, 1.00, 1),
            SIMD4<Float>(0.35, 0.92, 0.79, 1),
            SIMD4<Float>(1.00, 0.68, 0.50, 1),
            SIMD4<Float>(0.91, 0.54, 0.87, 1)
        ]
        return palette[Int(GraphRegionIdentity.hash(regionId) % UInt64(palette.count))]
    }

}
