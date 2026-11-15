import AppKit
import SpriteKit

@MainActor
final class GraphEdgeArtwork {
    private let confirmed = SKShapeNode()
    private let unconfirmed = SKShapeNode()
    private let arrows = SKShapeNode()
    private let emphasis = SKShapeNode()
    private let label = SKLabelNode(fontNamed: "Helvetica Neue")
    private var edgesById: [String: GraphEdge] = [:]
    private var positions: [String: CGPoint] = [:]
    private var focused = false

    func attach(to scene: SKScene) {
        confirmed.zPosition = -4
        unconfirmed.zPosition = -3
        arrows.zPosition = -2
        emphasis.zPosition = -1
        for layer in [confirmed, unconfirmed, arrows, emphasis] {
            layer.lineWidth = 1
            scene.addChild(layer)
        }
        label.fontSize = 11
        label.zPosition = 10
        label.isHidden = true
        scene.addChild(label)
    }

    func applyAppearance(dark: Bool, highContrast: Bool, cameraScale: CGFloat) {
        confirmed.strokeColor = (dark ? NSColor.lightGray : .secondaryLabelColor)
            .withAlphaComponent(highContrast ? 0.75 : 0.45)
        unconfirmed.strokeColor = NSColor.systemOrange
            .withAlphaComponent(highContrast ? 0.85 : 0.32)
        arrows.strokeColor = confirmed.strokeColor
        emphasis.strokeColor = .controlAccentColor
        label.fontColor = .labelColor
        setScale(cameraScale, highContrast: highContrast)
    }

    func setScale(_ scale: CGFloat, highContrast: Bool) {
        let width = max(0.7, scale * (highContrast ? 1.3 : 0.85))
        confirmed.lineWidth = width
        unconfirmed.lineWidth = width
        arrows.lineWidth = width
        emphasis.lineWidth = max(2, scale * (highContrast ? 2.8 : 2))
    }

    func rebuild(edges: [GraphEdge], positions: [String: CGPoint], focused: Bool,
                 selected: String?, hovered: String?) {
        edgesById = Dictionary(uniqueKeysWithValues: edges.map { ($0.relationId, $0) })
        self.positions = positions
        self.focused = focused
        let confirmedPath = CGMutablePath()
        let unconfirmedPath = CGMutablePath()
        let arrowPath = CGMutablePath()
        for edge in edges {
            guard let segment = segment(for: edge) else { continue }
            let proposed = edge.evidenceStatus == "suggested" ||
                edge.evidenceStatus == "contradicted"
            let path = proposed ? unconfirmedPath : confirmedPath
            path.move(to: segment.start)
            path.addLine(to: segment.end)
            if focused {
                let dx = segment.direction.dx
                let dy = segment.direction.dy
                let wing: CGFloat = 6
                arrowPath.move(to: segment.end)
                arrowPath.addLine(to: CGPoint(x: segment.end.x - dx * wing - dy * wing,
                                              y: segment.end.y - dy * wing + dx * wing))
                arrowPath.move(to: segment.end)
                arrowPath.addLine(to: CGPoint(x: segment.end.x - dx * wing + dy * wing,
                                              y: segment.end.y - dy * wing - dx * wing))
            }
        }
        confirmed.path = confirmedPath
        unconfirmed.path = unconfirmedPath
        arrows.path = arrowPath
        highlight(selected: selected, hovered: hovered)
    }

    func highlight(selected: String?, hovered: String?) {
        let path = CGMutablePath()
        for id in Set([selected, hovered].compactMap { $0 }) {
            guard let edge = edgesById[id], let segment = segment(for: edge) else { continue }
            path.move(to: segment.start)
            path.addLine(to: segment.end)
        }
        emphasis.path = path
        label.isHidden = true
        if let id = selected ?? hovered,
           let edge = edgesById[id], let segment = segment(for: edge) {
            label.text = KnowledgeVocabulary.label(edge.predicate)
            label.position = segment.midpoint
            label.isHidden = false
        }
    }

    private func segment(for edge: GraphEdge) ->
        (start: CGPoint, end: CGPoint, direction: CGVector, midpoint: CGPoint)? {
        guard let a = positions[edge.subjectId],
              let b = positions[edge.objectId] else { return nil }
        let dx = b.x - a.x
        let dy = b.y - a.y
        let distance = max(1, hypot(dx, dy))
        let ux = dx / distance
        let uy = dy / distance
        let inset: CGFloat = focused ? min(15, distance * 0.2) : min(4, distance * 0.2)
        return (
            CGPoint(x: a.x + ux * inset, y: a.y + uy * inset),
            CGPoint(x: b.x - ux * inset, y: b.y - uy * inset),
            CGVector(dx: ux, dy: uy),
            CGPoint(x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 + 12)
        )
    }
}
