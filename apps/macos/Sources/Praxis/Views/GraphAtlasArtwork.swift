import AppKit
import SpriteKit

@MainActor
final class GraphAtlasArtwork {
    var reduceMotion = false {
        didSet {
            for effect in effects { effect.isPaused = reduceMotion }
        }
    }

    private var shapes: [SKShapeNode] = []
    private var effects: [SKShapeNode] = []

    func clear() {
        for shape in shapes { shape.removeFromParent() }
        for effect in effects { effect.removeFromParent() }
        shapes.removeAll(keepingCapacity: true)
        effects.removeAll(keepingCapacity: true)
    }

    func draw(nodes: [GraphNode], positions: [String: CGPoint], radii: [String: CGFloat],
              degree: [String: Int], in scene: SKScene) {
        var grouped: [String: CGMutablePath] = [:]
        for node in nodes {
            guard let point = positions[node.entityId], let radius = radii[node.entityId] else { continue }
            let key = node.status == "suggested" ? "suggested" : (node.entityType ?? "other")
            if grouped[key] == nil { grouped[key] = CGMutablePath() }
            grouped[key]?.addEllipse(in: CGRect(x: point.x - radius, y: point.y - radius,
                                                width: radius * 2, height: radius * 2))
        }
        for key in grouped.keys.sorted() {
            guard let path = grouped[key] else { continue }
            let shape = SKShapeNode(path: path)
            shape.zPosition = 2
            shape.fillColor = GraphPalette.color(for: key, suggested: key == "suggested")
            shape.strokeColor = .clear
            shape.lineWidth = 0
            scene.addChild(shape)
            shapes.append(shape)
        }

        let hubs = nodes.sorted {
            let a = degree[$0.entityId, default: 0]
            let b = degree[$1.entityId, default: 0]
            return a == b ? $0.entityId < $1.entityId : a > b
        }
        for node in hubs.prefix(8) where degree[node.entityId, default: 0] >= 3 {
            guard let point = positions[node.entityId], let radius = radii[node.entityId] else { continue }
            let effect = SKShapeNode(circleOfRadius: radius * 1.8)
            effect.position = point
            effect.zPosition = 1
            effect.fillColor = .clear
            effect.strokeColor = GraphPalette.color(for: node.entityType, suggested: false)
            effect.lineWidth = 1.4
            effect.alpha = 0.2
            effect.run(.repeatForever(.sequence([
                .fadeAlpha(to: 0.4, duration: 2),
                .fadeAlpha(to: 0.12, duration: 2)
            ])))
            effect.isPaused = reduceMotion
            scene.addChild(effect)
            effects.append(effect)
        }
    }
}

enum GraphPalette {
    static func color(for type: String?, suggested: Bool) -> NSColor {
        if suggested { return .systemGray }
        switch type {
        case "person": return .systemOrange
        case "organization": return .systemPurple
        case "project": return .systemTeal
        case "technology": return .systemBlue
        case "concept": return .systemYellow
        case "document": return .systemGreen
        case "event": return .systemPink
        case "place": return .systemIndigo
        case "class": return .systemCyan
        case "function": return .systemOrange
        case "binary_module": return .systemRed
        case "data_asset": return .systemBrown
        default: return .secondaryLabelColor
        }
    }
}
