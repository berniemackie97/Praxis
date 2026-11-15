import AppKit
import SpriteKit

@MainActor
final class GraphZoneArtwork {
    private let root = SKNode()
    private var hubs: [String: SKNode] = [:]
    private var overview: GraphZoneOverview?
    private var selectedType: String?
    private var hoveredType: String?
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
        hoveredType = nil
        root.isHidden = true
    }

    func show(_ summary: GraphZoneOverview, only type: String?, dark: Bool, highContrast: Bool) {
        guard overview != summary || selectedType != type ||
              self.dark != dark || self.highContrast != highContrast else {
            root.isHidden = false
            return
        }
        clear()
        overview = summary
        selectedType = type
        self.dark = dark
        self.highContrast = highContrast
        let zones = summary.zones.filter { type == nil || $0.entityType == type }
        let ids = Set(zones.map(\.entityType))
        let links = summary.links.filter {
            ids.contains($0.subjectType) && ids.contains($0.objectType)
        }
        let visible = GraphZoneOverview(entityCount: zones.reduce(0) { $0 + $1.count },
                                        relationshipCount: links.reduce(0) { $0 + $1.count },
                                        zones: zones, links: links)
        placement = GraphZoneLayout.place(visible)
        for link in links where link.count > 0 {
            guard let a = placement.positions[link.subjectType],
                  let b = placement.positions[link.objectType] else { continue }
            let groups: [(Int, NSColor, CGFloat)] = [
                (link.confirmedCount, .systemTeal, 0),
                (link.suggestedCount, .systemOrange, 9),
                (link.contradictedCount, .systemRed, -9)
            ]
            for (count, tint, offset) in groups where count > 0 {
                let path = CGMutablePath()
                if link.subjectType == link.objectType {
                    let radius = (placement.radii[link.subjectType] ?? 30) + 15 + offset
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
        for zone in zones {
            guard let center = placement.positions[zone.entityType],
                  let radius = placement.radii[zone.entityType] else { continue }
            let tint = GraphPalette.color(for: zone.entityType, suggested: false)
            let hub = SKNode()
            hub.position = center
            hub.name = "zone:" + zone.entityType
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
            let density = SKShapeNode(path: Self.densityPath(type: zone.entityType,
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
            title.text = KnowledgeVocabulary.label(zone.entityType).uppercased()
            title.fontSize = 13
            title.fontColor = dark ? .white : .labelColor
            title.verticalAlignmentMode = .center
            title.position = CGPoint(x: 0, y: -radius * 0.49)
            title.zPosition = 3
            hub.addChild(title)
            let count = SKLabelNode(fontNamed: "Helvetica Neue")
            count.text = "\(zone.count.formatted()) entities"
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
            hubs[zone.entityType] = hub
        }
        root.isHidden = false
    }

    func hide() {
        root.isHidden = true
        hover(nil)
    }

    func hover(_ type: String?) {
        guard hoveredType != type else { return }
        hoveredType = type
        for (id, hub) in hubs {
            hub.alpha = type == nil || type == id ? 1 : 0.75
        }
    }

    func type(at point: CGPoint, scale: CGFloat) -> String? {
        var best: (type: String, distance: CGFloat)?
        for (id, center) in placement.positions {
            let radius = (placement.radii[id] ?? 30) + max(0, scale * 7)
            let distance = hypot(point.x - center.x, point.y - center.y)
            guard distance <= radius else { continue }
            if best == nil || distance < best!.distance { best = (id, distance) }
        }
        return best?.type
    }

    func nearest(to point: CGPoint, scale: CGFloat) -> String? {
        type(at: point, scale: scale)
    }

    func point(for type: String) -> CGPoint? { placement.positions[type] }

    private static func densityPath(type: String, count: Int, radius: CGFloat) -> CGPath {
        let path = CGMutablePath()
        var state = type.utf8.reduce(UInt64(14_695_981_039_346_656_037)) {
            ($0 ^ UInt64($1)) &* 1_099_511_628_211
        }
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
