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
        let ordered = overview.regions.sorted { $0.id < $1.id }
        for zone in ordered {
            guard let center = placement.zoneCenters[zone.id] else { continue }
            let color = NeuralNebulaLayout.color(for: zone.id)
            let size = placement.zoneSizes[zone.id] ?? 12
            points.append(NeuralNebulaVertex(position: center, color: color, size: size))
            let satellites = min(72, max(10, Int(log2(Double(zone.count) + 1) * 10)))
            for number in 0..<satellites {
                let radial = sqrt(Float(number + 1) / Float(satellites))
                let angle = Float(number) * 2.399963
                let fraction = (Float(number) + 0.5) / Float(satellites)
                let latitude: Float = 1 - 2 * fraction
                let plane = sqrt(max(0, 1 - latitude * latitude))
                let displacement = SIMD3<Float>(cos(angle) * plane, latitude,
                                                 sin(angle) * plane) * (size * 1.45 * radial)
                points.append(NeuralNebulaVertex(position: center + displacement,
                    color: SIMD4<Float>(color.x, color.y, color.z, highContrast ? 0.52 : 0.25),
                    size: highContrast ? 2.6 : 1.7))
            }
        }
        for number in 0..<220 {
            let fraction = (Float(number) + 0.5) / Float(220)
            let latitude: Float = 1 - 2 * fraction
            let angle = Float(number) * 2.399963
            let radial = sqrt(max(0, 1 - latitude * latitude))
            let position = SIMD3<Float>(cos(angle) * radial, latitude, sin(angle) * radial) * 197
            points.append(NeuralNebulaVertex(position: position,
                color: SIMD4<Float>(0.30, 0.55, 0.84, highContrast ? 0.21 : 0.10), size: 1.3))
        }

        for relation in overview.links {
            guard let a = placement.zoneCenters[relation.subjectId],
                  let b = placement.zoneCenters[relation.objectId],
                  relation.subjectId != relation.objectId else { continue }
            if relation.confirmedCount > 0 {
                appendLink(from: a, to: b, color: SIMD4<Float>(0.31, 0.77, 1, 0.47), into: &links)
            }
            if relation.suggestedCount > 0 {
                appendLink(from: a, to: b, color: SIMD4<Float>(0.98, 0.69, 0.31, 0.45), into: &links)
            }
            if relation.contradictedCount > 0 {
                appendLink(from: a, to: b, color: SIMD4<Float>(1, 0.37, 0.44, 0.53), into: &links)
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
