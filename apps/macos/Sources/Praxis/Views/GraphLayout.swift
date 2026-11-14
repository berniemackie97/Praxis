import CoreGraphics
import Foundation

/// Deterministic visual positions. A node's location never creates a knowledge relation.
struct GraphLayout {
    private(set) var positions: [String: CGPoint] = [:]
    private var velocities: [String: CGVector] = [:]
    private var settledSteps = 0

    mutating func configure(nodes: [GraphNode], focus: String?) {
        let ids = Set(nodes.map(\.entityId))
        positions = positions.filter { ids.contains($0.key) }
        velocities = velocities.filter { ids.contains($0.key) }
        for (index, node) in nodes.enumerated() where positions[node.entityId] == nil {
            let seed = Self.seed(node.entityId)
            let angle = CGFloat(seed % 10_000) / 10_000 * (.pi * 2)
            let radius = CGFloat(75 + (index % 5) * 24)
            positions[node.entityId] = CGPoint(x: cos(angle) * radius, y: sin(angle) * radius)
            velocities[node.entityId] = .zero
        }
        if let focus, ids.contains(focus) {
            positions[focus] = .zero
            velocities[focus] = .zero
        }
        settledSteps = 0
    }

    /// One bounded step of repulsion, real-edge springs and weak type grouping.
    /// Return false when the layout has settled, so the scene stops simulation.
    mutating func advance(nodes: [GraphNode], edges: [GraphEdge], focus: String?, reducedMotion: Bool) -> Bool {
        guard !reducedMotion, settledSteps < 180, nodes.count > 1 else { return false }
        let ids = nodes.map(\.entityId)
        var forces = Dictionary(uniqueKeysWithValues: ids.map { ($0, CGVector.zero) })
        for a in 0..<ids.count {
            for b in (a + 1)..<ids.count {
                guard let pa = positions[ids[a]], let pb = positions[ids[b]] else { continue }
                var dx = pa.x - pb.x
                var dy = pa.y - pb.y
                if abs(dx) + abs(dy) < 0.001 {
                    dx = CGFloat(a + 1) * 0.01
                    dy = CGFloat(b + 1) * -0.01
                }
                let distance = max(25, hypot(dx, dy))
                let strength = min(5, 5_000 / (distance * distance))
                let fx = dx / distance * strength
                let fy = dy / distance * strength
                forces[ids[a], default: .zero].dx += fx
                forces[ids[a], default: .zero].dy += fy
                forces[ids[b], default: .zero].dx -= fx
                forces[ids[b], default: .zero].dy -= fy
            }
        }
        for edge in edges {
            guard let start = positions[edge.subjectId], let end = positions[edge.objectId] else { continue }
            let dx = end.x - start.x
            let dy = end.y - start.y
            let distance = max(1, hypot(dx, dy))
            let strength = min(5, max(-5, (distance - 122) * 0.016))
            let fx = dx / distance * strength
            let fy = dy / distance * strength
            forces[edge.subjectId, default: .zero].dx += fx
            forces[edge.subjectId, default: .zero].dy += fy
            forces[edge.objectId, default: .zero].dx -= fx
            forces[edge.objectId, default: .zero].dy -= fy
        }
        var maxMovement: CGFloat = 0
        for node in nodes {
            let id = node.entityId
            guard id != focus, let point = positions[id] else { continue }
            let anchor = Self.anchor(for: node.entityType)
            let force = forces[id] ?? .zero
            let previous = velocities[id] ?? .zero
            let velocity = CGVector(
                dx: max(-7, min(7, (previous.dx + force.dx + (anchor.x - point.x) * 0.002) * 0.82)),
                dy: max(-7, min(7, (previous.dy + force.dy + (anchor.y - point.y) * 0.002) * 0.82)))
            velocities[id] = velocity
            positions[id] = CGPoint(x: point.x + velocity.dx, y: point.y + velocity.dy)
            maxMovement = max(maxMovement, hypot(velocity.dx, velocity.dy))
        }
        settledSteps += 1
        return maxMovement > 0.06 && settledSteps < 180
    }

    mutating func move(_ id: String, to point: CGPoint) {
        guard positions[id] != nil else { return }
        positions[id] = point
        velocities[id] = .zero
    }

    private static func anchor(for type: String?) -> CGPoint {
        let categories = KnowledgeVocabulary.entityTypes
        let index = categories.firstIndex(of: type ?? "") ?? categories.count - 1
        let angle = CGFloat(index) * (.pi * 2 / CGFloat(categories.count))
        return CGPoint(x: cos(angle) * 85, y: sin(angle) * 85)
    }

    private static func seed(_ value: String) -> UInt64 {
        value.utf8.reduce(UInt64(14_695_981_039_346_656_037)) { ($0 ^ UInt64($1)) &* 1_099_511_628_211 }
    }
}
