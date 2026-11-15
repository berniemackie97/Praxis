import Foundation
import simd

struct NeuralNebulaVertex {
    var position: SIMD3<Float>
    var color: SIMD4<Float>
    var size: Float
}

enum NeuralNebulaGeometry {
    struct Result {
        let zonePoints: [NeuralNebulaVertex]
        let zoneLines: [NeuralNebulaVertex]
        let detailPoints: [NeuralNebulaVertex]
        let detailLines: [NeuralNebulaVertex]
    }

    static func build(overview: GraphVisualOverview, placement: NeuralNebulaPlacement,
                      nodes graphNodes: [GraphNode], edges graphEdges: [GraphEdge],
                      selectedNodeId: String?, selectedEdgeId: String? = nil, highContrast: Bool) -> Result {
        var points: [NeuralNebulaVertex] = []
        var links: [NeuralNebulaVertex] = []
        for (index, zone) in overview.regions.enumerated() {
            guard let center = placement.zoneCenters[zone.id] else { continue }
            let forming = zone.memberCount == 0
            let color = NeuralNebulaLayout.regionColor(index, forming: forming)
            let size = placement.zoneSizes[zone.id] ?? 12
            var random = CortexRandom(seed: GraphRegionIdentity.hash(zone.id))
            // Each galaxy lies in its own tilted plane.
            // Mostly face-on discs with a small individual tilt, like galaxies seen across a sky map.
            let tilt = random.next() * 0.9 - 0.45, turn = random.next() * 6.283
            let axisA = SIMD3<Float>(cos(turn), sin(turn), sin(tilt) * 0.5)
            let axisB = simd_normalize(simd_cross(SIMD3<Float>(sin(tilt) * 0.4, 0, 1), axisA))
            if forming {
                // Uncondensed nebula: gas only, no stars, until its sources become knowledge.
                for _ in 0..<26 {
                    let offset = (axisA * (random.next() * 2 - 1) + axisB * (random.next() * 2 - 1)) * size * 2.2
                    points.append(NeuralNebulaVertex(position: center + offset,
                        color: SIMD4<Float>(color.x, color.y, color.z, highContrast ? 0.22 : 0.10), size: size * (0.8 + random.next())))
                }
                continue
            }
            points.append(NeuralNebulaVertex(position: center, color: SIMD4<Float>(color.x, color.y, color.z, 0.55), size: size * 1.6))
            points.append(NeuralNebulaVertex(position: center, color: SIMD4<Float>(1, 0.97, 0.92, 0.95), size: size * 0.45))
            let arms = 2 + Int(random.next() * 2)
            let stars = min(300, max(36, Int(log2(Double(zone.count) + 1) * 26)))
            for number in 0..<stars {
                let arm = number % arms
                let along = pow(random.next(), 0.7)
                let angle = Float(arm) * (6.283 / Float(arms)) + along * 4.2 + (random.next() - 0.5) * 0.5 * (1.2 - along)
                let radius = along * size * 1.9
                let offset = axisA * cos(angle) * radius + axisB * sin(angle) * radius * 0.92
                    + simd_cross(axisA, axisB) * (random.next() - 0.5) * size * 0.25
                let bright = number % 5 == 0
                points.append(NeuralNebulaVertex(position: center + offset,
                    color: bright ? SIMD4<Float>(0.95, 0.96, 1, highContrast ? 0.95 : 0.8)
                                  : SIMD4<Float>(color.x, color.y, color.z, highContrast ? 0.75 : 0.55),
                    size: bright ? 2.6 : (highContrast ? 2.4 : 1.8)))
            }
        }
        var sky = CortexRandom(seed: 99)
        // Ornamental nebula clouds behind the universe.
        for cloud in 0..<9 {
            let tint: SIMD3<Float> = cloud % 3 == 0 ? [0.42, 0.20, 0.75] : cloud % 3 == 1 ? [0.10, 0.32, 0.55] : [0.45, 0.12, 0.45]
            let direction = simd_normalize(SIMD3<Float>(sky.next() * 2 - 1, sky.next() - 0.5, -0.6 - sky.next()))
            points.append(NeuralNebulaVertex(position: direction * 700, color: SIMD4<Float>(tint, 0.10), size: 520 + sky.next() * 420))
        }
        for _ in 0..<900 {
            let direction = simd_normalize(SIMD3<Float>(sky.next() * 2 - 1, sky.next() * 2 - 1, sky.next() * 2 - 1))
            points.append(NeuralNebulaVertex(position: direction * (520 + sky.next() * 400),
                color: SIMD4<Float>(0.82, 0.86, 1.0, (highContrast ? 0.35 : 0.18) + sky.next() * 0.25), size: 1 + sky.next() * 1.6))
        }

        let strongest = Float(max(1, overview.links.map(\.count).max() ?? 1))
        let colors = Dictionary(uniqueKeysWithValues: overview.regions.enumerated().map {
            ($0.element.id, NeuralNebulaLayout.regionColor($0.offset, forming: $0.element.memberCount == 0))
        })
        for relation in overview.links {
            guard let a = placement.zoneCenters[relation.subjectId],
                  let b = placement.zoneCenters[relation.objectId],
                  relation.subjectId != relation.objectId else { continue }
            // Filaments: curved light lanes whose brightness is the evidence between galaxies. Confirmed
            // evidence takes the galaxies' colours; suggested (amber) and contradicted (red) lanes stay
            // distinct and bend away so they are never mistaken for confirmed links.
            let colorA = colors[relation.subjectId] ?? [0.55, 0.81, 1, 1], colorB = colors[relation.objectId] ?? [0.55, 0.81, 1, 1]
            let lanes: [(Int, SIMD4<Float>?, Float)] = [
                (relation.confirmedCount, nil, 0.12),
                (relation.suggestedCount, SIMD4<Float>(0.98, 0.69, 0.31, 1), 0.2),
                (relation.contradictedCount, SIMD4<Float>(1, 0.37, 0.44, 1), 0.04)
            ]
            for (count, fixed, bend) in lanes where count > 0 {
                let weight = sqrt(Float(count) / strongest)
                let control = (a + b) * 0.5 + SIMD3<Float>(0, simd_length(b - a) * bend, 0)
                let segments = 18
                for step in 0..<segments {
                    for t in [Float(step) / Float(segments), Float(step + 1) / Float(segments)] {
                        let u = 1 - t
                        let point = u * u * a + 2 * u * t * control + t * t * b
                        var color = fixed ?? (colorA * u + colorB * t)
                        color.w = (highContrast ? 0.5 : 0.22) + 0.5 * weight
                        links.append(NeuralNebulaVertex(position: point, color: color, size: 1))
                    }
                }
            }
        }

        let localPoints = detailPoints(nodes: graphNodes, placement: placement,
                                       selectedNodeId: selectedNodeId, highContrast: highContrast)
        let localLinks = detailLines(edges: graphEdges, placement: placement,
                                     selectedEdgeId: selectedEdgeId, highContrast: highContrast)
        return Result(zonePoints: points, zoneLines: links,
                      detailPoints: localPoints, detailLines: localLinks)
    }

    static func detailPoints(nodes: [GraphNode], placement: NeuralNebulaPlacement,
                             selectedNodeId: String?, highContrast: Bool) -> [NeuralNebulaVertex] {
        nodes.compactMap { node in
            guard let position = placement.nodeCenters[node.entityId] else { return nil }
            let highlighted = node.entityId == selectedNodeId
            let color = highlighted ? SIMD4<Float>(1, 1, 1, 1) :
                NeuralNebulaLayout.color(for: node.entityType ?? "other")
            return NeuralNebulaVertex(position: position, color: color,
                                      size: highlighted ? 10 : (highContrast ? 6.5 : 4.7))
        }
    }

    static func detailLines(edges: [GraphEdge], placement: NeuralNebulaPlacement,
                            selectedEdgeId: String?, highContrast: Bool) -> [NeuralNebulaVertex] {
        var links: [NeuralNebulaVertex] = []
        for edge in edges {
            guard let a = placement.nodeCenters[edge.subjectId],
                  let b = placement.nodeCenters[edge.objectId] else { continue }
            let color: SIMD4<Float>
            if edge.relationId == selectedEdgeId {
                color = SIMD4<Float>(1, 1, 1, 0.95)
            } else {
                switch edge.evidenceStatus {
                case "suggested": color = SIMD4<Float>(1, 0.71, 0.33, 0.49)
                case "contradicted": color = SIMD4<Float>(1, 0.41, 0.48, 0.58)
                default: color = SIMD4<Float>(0.55, 0.81, 1, highContrast ? 0.73 : 0.40)
                }
            }
            appendLink(from: a, to: b, color: color, into: &links)
        }
        return links
    }

    private static func appendLink(from a: SIMD3<Float>, to b: SIMD3<Float>,
                            color: SIMD4<Float>, into vertices: inout [NeuralNebulaVertex]) {
        guard simd_length_squared(b - a) > 0.001 else { return }
        vertices.append(NeuralNebulaVertex(position: a, color: color, size: 1))
        vertices.append(NeuralNebulaVertex(position: b, color: color, size: 1))
    }

}
