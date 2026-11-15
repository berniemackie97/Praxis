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
        for link in links where link.count > 0 {
            guard let a = placement.positions[link.subjectId],
                  let b = placement.positions[link.objectId] else { continue }
            let groups: [(Int, NSColor, CGFloat)] = [
                (link.confirmedCount, .systemTeal, 0),
                (link.suggestedCount, .systemOrange, 9),
                (link.contradictedCount, .systemRed, -9)
            ]
            for (count, tint, offset) in groups where count > 0 {
                let path = CGMutablePath()
                if link.subjectId == link.objectId {
                    let radius = (placement.radii[link.subjectId] ?? 30) + 15 + offset
                    path.addEllipse(in: CGRect(x: a.x - radius, y: a.y - radius,
                                               width: radius * 2, height: radius * 2))
                } else {
                    let dx = b.x - a.x
                    let dy = b.y - a.y
                    let length = max(1, hypot(dx, dy))
                    let side = CGPoint(x: -dy / length * offset, y: dx / length * offset)
                    path.move(to: CGPoint(x: a.x + side.x, y: a.y + side.y))
                    let bend = min(36, length * 0.12) + offset
                    let control = CGPoint(x: (a.x + b.x) / 2 - dy / length * bend,
                                          y: (a.y + b.y) / 2 + dx / length * bend)
                    path.addQuadCurve(to: CGPoint(x: b.x + side.x, y: b.y + side.y),
                                      control: control)
                }
                let stroke = SKShapeNode(path: path)
                stroke.strokeColor = tint.withAlphaComponent(highContrast ? 0.7 : 0.34)
                stroke.lineWidth = min(5, max(1.1, sqrt(CGFloat(count)) * 0.78))
                stroke.zPosition = -2
                root.addChild(stroke)
            }
        }
        for zone in regions {
            guard let center = placement.positions[zone.id],
                  let radius = placement.radii[zone.id] else { continue }
            let hue = CGFloat(GraphRegionIdentity.hash(zone.id) % 360) / 360
            let tint = NSColor(calibratedHue: hue, saturation: highContrast ? 0.73 : 0.61,
                               brightness: dark ? 0.93 : 0.76, alpha: 1)
            let hub = SKNode()
            hub.position = center
            hub.name = "zone:" + zone.id
            let halo = SKShapeNode(circleOfRadius: radius * 1.38)
            halo.fillColor = tint.withAlphaComponent(dark ? 0.065 : 0.04)
            halo.strokeColor = tint.withAlphaComponent(highContrast ? 0.55 : 0.17)
            halo.lineWidth = highContrast ? 2.6 : 1
            halo.zPosition = 0
            hub.addChild(halo)
            let ring = SKShapeNode(circleOfRadius: radius)
            ring.fillColor = tint.withAlphaComponent(dark ? 0.26 : 0.13)
            ring.strokeColor = tint.withAlphaComponent(highContrast ? 1 : 0.78)
            ring.lineWidth = highContrast ? 3 : 1.7
            ring.zPosition = 1
            hub.addChild(ring)
            let density = SKShapeNode(path: Self.densityPath(regionId: zone.id,
                                                             count: zone.count, radius: radius))
            density.fillColor = tint.withAlphaComponent(dark ? 0.68 : 0.47)
            density.strokeColor = .clear
            density.lineWidth = 0
            density.zPosition = 1.5
            hub.addChild(density)
            let core = SKShapeNode(circleOfRadius: max(9, radius * 0.20))
            core.fillColor = tint
            core.strokeColor = .clear
            core.zPosition = 2
            hub.addChild(core)
            let title = SKLabelNode(fontNamed: "Helvetica Neue Medium")
            title.text = String(zone.title.prefix(32)).uppercased()
            title.fontSize = 13
            title.fontColor = dark ? .white : .labelColor
            title.verticalAlignmentMode = .center
            title.position = CGPoint(x: 0, y: -radius * 0.49)
            title.zPosition = 3
            hub.addChild(title)
            let count = SKLabelNode(fontNamed: "Helvetica Neue")
            count.text = "\(zone.memberCount.formatted()) explicit members"
            count.fontSize = 10
            count.fontColor = dark ? NSColor.white.withAlphaComponent(0.73) : .secondaryLabelColor
            count.position = CGPoint(x: 0, y: -radius * 0.49 - 17)
            count.zPosition = 3
            hub.addChild(count)
            hub.run(.repeatForever(.sequence([
                .scale(to: 1.035, duration: 3.5),
                .scale(to: 1, duration: 3.5)
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

    private static func densityPath(regionId: String, count: Int, radius: CGFloat) -> CGPath {
        let path = CGMutablePath()
        var state = GraphRegionIdentity.hash(regionId)
        let samples = min(96, max(6, Int(log2(Double(count) + 1) * 12)))
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
