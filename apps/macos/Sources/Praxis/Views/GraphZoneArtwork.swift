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
            if link.subjectType == link.objectType {
                let radius = (placement.radii[link.subjectType] ?? 30) + 15
                let loop = SKShapeNode(circleOfRadius: radius)
                loop.position = a
                loop.fillColor = .clear
                loop.strokeColor = (link.confirmedCount > 0 ? NSColor.systemTeal : .systemOrange)
                    .withAlphaComponent(highContrast ? 0.65 : 0.28)
                loop.lineWidth = min(5, max(1.5, sqrt(CGFloat(link.count)) * 0.9))
                loop.zPosition = -1
                root.addChild(loop)
            } else {
                let path = CGMutablePath()
                path.move(to: a)
                path.addLine(to: b)
                let line = SKShapeNode(path: path)
                line.strokeColor = (link.confirmedCount > 0 ? NSColor.lightGray : .systemOrange)
                    .withAlphaComponent(highContrast ? 0.7 : 0.3)
                line.lineWidth = min(6, max(1.2, sqrt(CGFloat(link.count)) * 0.85))
                line.zPosition = -2
                root.addChild(line)
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

    func hide() { root.isHidden = true }

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
}
