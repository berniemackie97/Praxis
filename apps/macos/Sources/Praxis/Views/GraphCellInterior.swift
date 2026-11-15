import AppKit
import SpriteKit

/// Cellular Atlas inside a region: the region is one cell seen from within. Its best-known entity is
/// the nucleus, the other entities are organelles sized by their connections, and relations are the
/// filaments between them. Membrane, cytoplasm drift and reticulum are ornamental.
@MainActor
final class GraphCellInterior {
    var reduceMotion = false {
        didSet { root.isPaused = reduceMotion }
    }

    private let root = SKNode()

    /// Nucleus at the centre, organelles in rings by importance (best-known nearest the nucleus),
    /// spaced so none overlap. Depends only on the nodes, so the same region always looks the same.
    static func arrange(_ nodes: [GraphNode], radii: [String: CGFloat]) -> [String: CGPoint] {
        let ordered = nodes.sorted { $0.mentions == $1.mentions ? $0.entityId < $1.entityId : $0.mentions > $1.mentions }
        guard let nucleus = ordered.first else { return [:] }
        var positions: [String: CGPoint] = [nucleus.entityId: .zero]
        var ring: CGFloat = 95
        var angle: CGFloat = 0
        var placedOnRing: CGFloat = 0
        for node in ordered.dropFirst() {
            let size = max(4, (radii[node.entityId] ?? 4) * 2.4) * 2.6 + 10
            if placedOnRing + size > 2 * .pi * ring {
                ring += 46
                placedOnRing = 0
                angle = CGFloat(GraphRegionIdentity.hash(node.entityId) % 628) / 100
            }
            angle += size / ring
            placedOnRing += size
            positions[node.entityId] = CGPoint(x: cos(angle) * ring, y: sin(angle) * ring)
        }
        return positions
    }

    func clear() {
        root.removeAllChildren()
        root.removeFromParent()
    }

    func draw(nodes: [GraphNode], positions: [String: CGPoint], radii: [String: CGFloat],
              tint: NSColor, dark: Bool, in scene: SKScene) {
        clear()
        guard !positions.isEmpty else { return }
        root.zPosition = -3
        scene.addChild(root)
        let points = Array(positions.values)
        let center = CGPoint(x: (points.map(\.x).min()! + points.map(\.x).max()!) / 2,
                             y: (points.map(\.y).min()! + points.map(\.y).max()!) / 2)
        let reach = points.map { hypot($0.x - center.x, $0.y - center.y) }.max() ?? 100
        let radius = reach + 90
        var random = CortexRandom(seed: GraphRegionIdentity.hash(nodes.first?.entityId ?? "cell"))

        let membrane = Self.membrane(center: center, radius: radius, random: &random)
        let cytoplasm = SKShapeNode(path: membrane)
        cytoplasm.fillColor = tint.withAlphaComponent(dark ? 0.07 : 0.05)
        cytoplasm.strokeColor = .clear
        root.addChild(cytoplasm)
        for (scale, alpha, width, glow) in [(1.0, 0.9, 3.0, 10.0), (0.975, 0.45, 1.2, 0.0)] as [(CGFloat, CGFloat, CGFloat, CGFloat)] {
            let wall = SKShapeNode(path: Self.membrane(center: center, radius: radius * scale, random: &random))
            wall.strokeColor = tint.withAlphaComponent(alpha)
            wall.lineWidth = width
            wall.glowWidth = dark ? glow : 0
            wall.fillColor = .clear
            root.addChild(wall)
        }

        // Ornamental reticulum: wavy folds around the cell's centre.
        for fold in 0..<6 {
            let path = CGMutablePath()
            let base = radius * (0.22 + 0.06 * CGFloat(fold))
            for step in 0...60 {
                let angle = CGFloat(step) / 60 * .pi * 1.4 + CGFloat(fold)
                let wobble = base * (1 + 0.06 * sin(angle * 9 + CGFloat(fold)))
                let point = CGPoint(x: center.x + cos(angle) * wobble, y: center.y + sin(angle) * wobble)
                if step == 0 { path.move(to: point) } else { path.addLine(to: point) }
            }
            let reticulum = SKShapeNode(path: path)
            reticulum.strokeColor = tint.withAlphaComponent(0.16)
            reticulum.lineWidth = 1
            root.addChild(reticulum)
        }

        // Ornamental cytoplasm: vesicles and ribosomes drifting slowly.
        for _ in 0..<150 {
            let angle = CGFloat(random.next()) * 2 * .pi
            let distance = sqrt(CGFloat(random.next())) * radius * 0.94
            let size = CGFloat(0.8 + random.next() * 2.4)
            let speck = SKShapeNode(circleOfRadius: size)
            speck.fillColor = tint.withAlphaComponent(0.25 + CGFloat(random.next()) * 0.35)
            speck.strokeColor = .clear
            speck.position = CGPoint(x: center.x + cos(angle) * distance, y: center.y + sin(angle) * distance)
            let drift = CGVector(dx: CGFloat(random.next() - 0.5) * 22, dy: CGFloat(random.next() - 0.5) * 22)
            let duration = 4 + Double(random.next()) * 5
            speck.run(.repeatForever(.sequence([.move(by: drift, duration: duration),
                                                .move(by: CGVector(dx: -drift.dx, dy: -drift.dy), duration: duration)])))
            root.addChild(speck)
        }

        // Organelles: the nucleus is the region's best-known entity; others take shapes by identity.
        let nucleusId = nodes.max { $0.mentions == $1.mentions ? $0.entityId > $1.entityId : $0.mentions < $1.mentions }?.entityId
        for node in nodes {
            guard let point = positions[node.entityId] else { continue }
            let size = max(4, (radii[node.entityId] ?? 4) * 2.4)
            let organelle = node.entityId == nucleusId
                ? Self.nucleus(size: max(40, size * 3), dark: dark)
                : Self.organelle(kind: Int(GraphRegionIdentity.hash(node.entityId) % 3), size: size, tint: tint, dark: dark,
                                 angle: CGFloat(GraphRegionIdentity.hash(node.entityId) % 628) / 100)
            organelle.position = point
            organelle.zPosition = 1
            organelle.alpha = node.status == "suggested" ? 0.55 : 1
            root.addChild(organelle)
        }
        root.isPaused = reduceMotion
    }

    private static func nucleus(size: CGFloat, dark: Bool) -> SKNode {
        let node = SKNode()
        let envelope = SKShapeNode(circleOfRadius: size)
        envelope.fillColor = NSColor(calibratedRed: 1, green: 0.55, blue: 0.22, alpha: 0.32)
        envelope.strokeColor = NSColor(calibratedRed: 1, green: 0.75, blue: 0.45, alpha: 0.95)
        envelope.lineWidth = 2
        envelope.glowWidth = dark ? 8 : 0
        node.addChild(envelope)
        let nucleolus = SKShapeNode(circleOfRadius: size * 0.36)
        nucleolus.fillColor = NSColor(calibratedRed: 1, green: 0.82, blue: 0.6, alpha: 0.9)
        nucleolus.strokeColor = .clear
        nucleolus.position = CGPoint(x: size * 0.2, y: size * 0.12)
        node.addChild(nucleolus)
        return node
    }

    private static func organelle(kind: Int, size: CGFloat, tint: NSColor, dark: Bool, angle: CGFloat) -> SKNode {
        let node = SKNode()
        node.zRotation = angle
        switch kind {
        case 0: // mitochondrion: capsule with cristae
            let body = SKShapeNode(rect: CGRect(x: -size * 1.4, y: -size * 0.6, width: size * 2.8, height: size * 1.2), cornerRadius: size * 0.6)
            body.fillColor = tint.withAlphaComponent(0.28)
            body.strokeColor = tint.withAlphaComponent(0.95)
            body.lineWidth = 1.3
            body.glowWidth = dark ? 2 : 0
            node.addChild(body)
            let cristae = CGMutablePath()
            for step in -2...2 {
                let x = CGFloat(step) * size * 0.45
                cristae.move(to: CGPoint(x: x, y: -size * 0.45))
                cristae.addQuadCurve(to: CGPoint(x: x, y: size * 0.45), control: CGPoint(x: x + size * 0.3, y: 0))
            }
            let folds = SKShapeNode(path: cristae)
            folds.strokeColor = tint.withAlphaComponent(0.7)
            folds.lineWidth = 0.8
            node.addChild(folds)
        case 1: // vesicle: membrane sphere with lumen
            let outer = SKShapeNode(circleOfRadius: size)
            outer.fillColor = tint.withAlphaComponent(0.22)
            outer.strokeColor = tint.withAlphaComponent(0.95)
            outer.lineWidth = 1.3
            outer.glowWidth = dark ? 2 : 0
            node.addChild(outer)
            let lumen = SKShapeNode(circleOfRadius: size * 0.55)
            lumen.fillColor = .clear
            lumen.strokeColor = tint.withAlphaComponent(0.55)
            node.addChild(lumen)
        default: // Golgi stack: nested arcs
            for layer in 0..<4 {
                let arc = CGMutablePath()
                let r = size * (0.9 + CGFloat(layer) * 0.32)
                arc.addArc(center: CGPoint(x: 0, y: -size * 0.8), radius: r, startAngle: .pi * 0.25, endAngle: .pi * 0.75, clockwise: false)
                let cisterna = SKShapeNode(path: arc)
                cisterna.strokeColor = tint.withAlphaComponent(0.95 - CGFloat(layer) * 0.15)
                cisterna.lineWidth = 2.2
                cisterna.glowWidth = dark ? 1.5 : 0
                node.addChild(cisterna)
            }
        }
        return node
    }

    private static func membrane(center: CGPoint, radius: CGFloat, random: inout CortexRandom) -> CGPath {
        let phases = (0..<3).map { _ in CGFloat(random.next()) * 6.283 }
        let path = CGMutablePath()
        for step in 0...96 {
            let angle = CGFloat(step) / 96 * 2 * .pi
            let wobble = 1 + 0.04 * sin(3 * angle + phases[0]) + 0.025 * sin(5 * angle + phases[1]) + 0.015 * sin(9 * angle + phases[2])
            let point = CGPoint(x: center.x + cos(angle) * radius * wobble, y: center.y + sin(angle) * radius * wobble)
            if step == 0 { path.move(to: point) } else { path.addLine(to: point) }
        }
        path.closeSubpath()
        return path
    }
}
