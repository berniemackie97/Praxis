import AppKit
import SpriteKit

@MainActor
final class GraphZoneArtwork {
    private let root = SKNode()
    private var hubs: [String: SKNode] = [:]
    private var overview: GraphVisualOverview?

    private var hoveredRegionId: String?
    private var placement = GraphZonePlacement(positions: [:], radii: [:], bounds: .zero)
    private var dark = false
    private var highContrast = false
    var reduceMotion = false {
        didSet { for hub in hubs.values { hub.isPaused = reduceMotion } }
    }

    var isVisible: Bool { root.parent != nil && !root.isHidden }
    var bounds: CGRect { placement.bounds }

    func attach(to scene: SKScene) {
        root.zPosition = 0
        scene.addChild(root)
        root.isHidden = true
    }

    func clear() {
        root.removeAllChildren()
        hubs.removeAll(keepingCapacity: true)
        overview = nil
        hoveredRegionId = nil
        root.isHidden = true
    }

    func show(_ summary: GraphVisualOverview, dark: Bool, highContrast: Bool) {
        guard overview != summary || self.dark != dark || self.highContrast != highContrast else {
            root.isHidden = false
            return
        }
        clear()
        overview = summary
        self.dark = dark
        self.highContrast = highContrast
        let regions = summary.regions
        let links = summary.links
        placement = GraphZoneLayout.place(summary)
        let colors = Dictionary(uniqueKeysWithValues: regions.enumerated().map { index, zone -> (String, NSColor) in
            let rgb = zone.memberCount == 0 ? BrainPalette.forming : BrainPalette.hues[index % BrainPalette.hues.count]
            return (zone.id, NSColor(calibratedRed: CGFloat(rgb.x), green: CGFloat(rgb.y), blue: CGFloat(rgb.z), alpha: 1))
        })
        // Gap junctions: evidence between cells, drawn membrane to membrane with bright pores.
        for link in links where link.count > 0 && link.subjectId != link.objectId {
            guard let a = placement.positions[link.subjectId], let b = placement.positions[link.objectId],
                  let ra = placement.radii[link.subjectId], let rb = placement.radii[link.objectId] else { continue }
            let dx = b.x - a.x, dy = b.y - a.y
            let length = max(1, hypot(dx, dy))
            let ux = dx / length, uy = dy / length
            let start = CGPoint(x: a.x + ux * ra, y: a.y + uy * ra)
            let end = CGPoint(x: b.x - ux * rb, y: b.y - uy * rb)
            let path = CGMutablePath()
            path.move(to: start)
            path.addCurve(to: end, control1: CGPoint(x: start.x + ux * 40 - uy * 18, y: start.y + uy * 40 + ux * 18),
                          control2: CGPoint(x: end.x - ux * 40 + uy * 18, y: end.y - uy * 40 - ux * 18))
            let tint = (colors[link.subjectId] ?? .systemTeal).blended(withFraction: 0.5, of: colors[link.objectId] ?? .systemTeal) ?? .systemTeal
            let strand = SKShapeNode(path: path)
            strand.strokeColor = tint.withAlphaComponent(highContrast ? 0.9 : 0.62)
            strand.lineWidth = min(4, max(1.2, sqrt(CGFloat(link.count)) * 0.6))
            strand.glowWidth = dark ? 3 : 0
            strand.zPosition = -2
            root.addChild(strand)
            for pore in [start, end] {
                let dot = SKShapeNode(circleOfRadius: 3.2)
                dot.fillColor = .white
                dot.strokeColor = .clear
                dot.glowWidth = dark ? 4 : 0
                dot.position = pore
                dot.zPosition = 4
                root.addChild(dot)
            }
        }
        for zone in regions {
            guard let center = placement.positions[zone.id],
                  let radius = placement.radii[zone.id] else { continue }
            let forming = zone.memberCount == 0
            let tint = colors[zone.id] ?? .systemTeal
            let hub = SKNode()
            hub.position = center
            hub.name = "zone:" + zone.id
            let membranePath = Self.membrane(regionId: zone.id, radius: radius)
            let cytoplasm = SKShapeNode(path: membranePath)
            cytoplasm.fillColor = tint.withAlphaComponent(forming ? 0.04 : dark ? 0.12 : 0.08)
            cytoplasm.strokeColor = .clear
            cytoplasm.zPosition = 0
            hub.addChild(cytoplasm)
            let outer = SKShapeNode(path: forming ? membranePath.copy(dashingWithPhase: 0, lengths: [7, 6]) : membranePath)
            outer.strokeColor = tint.withAlphaComponent(highContrast ? 1 : 0.9)
            outer.lineWidth = highContrast ? 2.6 : 1.8
            outer.glowWidth = dark && !forming ? 5 : 0
            outer.fillColor = .clear
            outer.zPosition = 1
            hub.addChild(outer)
            if !forming {
                let inner = SKShapeNode(path: Self.membrane(regionId: zone.id, radius: radius * 0.93))
                inner.strokeColor = tint.withAlphaComponent(0.38)
                inner.lineWidth = 0.8
                inner.fillColor = .clear
                inner.zPosition = 1
                hub.addChild(inner)
            }
            let density = SKShapeNode(path: Self.densityPath(regionId: zone.id, count: zone.count, radius: radius))
            density.fillColor = tint.withAlphaComponent(forming ? 0.25 : dark ? 0.75 : 0.5)
            density.strokeColor = .clear
            density.zPosition = 1.5
            hub.addChild(density)
            if !forming {
                // Ornamental nucleus; the region's members and relations appear when it is entered.
                let nucleus = SKShapeNode(circleOfRadius: max(7, radius * 0.17))
                nucleus.fillColor = NSColor(calibratedRed: 1, green: 0.6, blue: 0.24, alpha: 0.9)
                nucleus.strokeColor = NSColor(calibratedRed: 1, green: 0.78, blue: 0.5, alpha: 0.9)
                nucleus.glowWidth = dark ? 6 : 0
                nucleus.position = CGPoint(x: radius * 0.12, y: radius * 0.1)
                nucleus.zPosition = 2
                hub.addChild(nucleus)
            }
            let title = SKLabelNode(fontNamed: "Helvetica Neue Bold")
            title.text = String(zone.title.prefix(32)).uppercased()
            title.fontSize = 13
            title.fontColor = dark ? tint : tint.blended(withFraction: 0.35, of: .black)
            title.verticalAlignmentMode = .center
            title.position = CGPoint(x: 0, y: -radius - 16)
            title.zPosition = 3
            hub.addChild(title)
            let count = SKLabelNode(fontNamed: "Menlo")
            count.text = forming ? "dividing · not knowledge yet" : "\(zone.memberCount.formatted()) entities"
            count.fontSize = 10
            count.fontColor = dark ? NSColor.white.withAlphaComponent(0.75) : .secondaryLabelColor
            count.position = CGPoint(x: 0, y: -radius - 32)
            count.zPosition = 3
            hub.addChild(count)
            hub.run(.repeatForever(.sequence([
                .scale(to: 1.03, duration: 3.2),
                .scale(to: 1, duration: 3.2)
            ])))
            hub.isPaused = reduceMotion
            root.addChild(hub)
            hubs[zone.id] = hub
        }
        root.isHidden = false
    }

    func hide() {
        root.isHidden = true
        hover(nil)
    }

    func hover(_ regionId: String?) {
        guard hoveredRegionId != regionId else { return }
        hoveredRegionId = regionId
        for (id, hub) in hubs {
            hub.alpha = regionId == nil || regionId == id ? 1 : 0.75
        }
    }

    func region(at point: CGPoint, scale: CGFloat) -> String? {
        var best: (id: String, distance: CGFloat)?
        for (id, center) in placement.positions {
            let radius = (placement.radii[id] ?? 30) + max(0, scale * 7)
            let distance = hypot(point.x - center.x, point.y - center.y)
            guard distance <= radius else { continue }
            if distance < (best?.distance ?? .infinity) { best = (id, distance) }
        }
        return best?.id
    }

    func nearest(to point: CGPoint, scale: CGFloat) -> String? {
        region(at: point, scale: scale)
    }

    func point(for regionId: String) -> CGPoint? { placement.positions[regionId] }

    /// A living, slightly irregular membrane: a circle perturbed by three low harmonics per cell.
    private static func membrane(regionId: String, radius: CGFloat) -> CGPath {
        var random = CortexRandom(seed: GraphRegionIdentity.hash(regionId))
        let phases = (0..<3).map { _ in CGFloat(random.next()) * 6.283 }
        let path = CGMutablePath()
        for step in 0...72 {
            let angle = CGFloat(step) / 72 * 2 * .pi
            let wobble = 1 + 0.05 * sin(3 * angle + phases[0]) + 0.035 * sin(5 * angle + phases[1]) + 0.02 * sin(9 * angle + phases[2])
            let point = CGPoint(x: cos(angle) * radius * wobble, y: sin(angle) * radius * wobble)
            if step == 0 { path.move(to: point) } else { path.addLine(to: point) }
        }
        path.closeSubpath()
        return path
    }

    private static func densityPath(regionId: String, count: Int, radius: CGFloat) -> CGPath {
        let path = CGMutablePath()
        var state = GraphRegionIdentity.hash(regionId)
        let samples = min(220, max(12, Int(log2(Double(count) + 1) * 22)))
        for _ in 0..<samples {
            state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            let direction = CGFloat((state >> 32) & 0xFFFF_FFFF) / CGFloat(UInt32.max)
            state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            let distance = sqrt(CGFloat((state >> 32) & 0xFFFF_FFFF) / CGFloat(UInt32.max))
            let angle = direction * 2 * .pi
            let point = CGPoint(x: cos(angle) * distance * radius * 0.85,
                                y: sin(angle) * distance * radius * 0.85)
            let size: CGFloat = 1.2
            path.addEllipse(in: CGRect(x: point.x - size, y: point.y - size,
                                       width: size * 2, height: size * 2))
        }
        return path
    }

}
