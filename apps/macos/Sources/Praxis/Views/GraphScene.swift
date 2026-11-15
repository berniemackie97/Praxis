import AppKit
import SpriteKit

@MainActor
final class GraphScene: SKScene {
    var didSelectNode: ((String?) -> Void)?
    var didSelectEdge: ((String?) -> Void)?
    var didSelectZone: ((String) -> Void)?
    var didReturnToZones: (() -> Void)?
    var reduceMotion = false {
        didSet {
            atlasArtwork.reduceMotion = reduceMotion
            cellInterior.reduceMotion = reduceMotion
            zoneArtwork.reduceMotion = reduceMotion
        }
    }

    private var graphNodes: [GraphNode] = []
    private var graphEdges: [GraphEdge] = []
    private var nodesById: [String: GraphNode] = [:]
    private var nodeRadii: [String: CGFloat] = [:]
    private var layout = GraphLayout()
    private var focusId: String?
    private var selectedNodeId: String?
    private var selectedEdgeId: String?
    private var hoveredNodeId: String?
    private var hoveredEdgeId: String?
    private var nodeShapes: [String: SKShapeNode] = [:]
    private let atlasArtwork = GraphAtlasArtwork()
    private let cellInterior = GraphCellInterior()
    /// The entered region's colour; when set, an entered region is drawn as the inside of a cell.
    var interiorTint: NSColor?
    private let zoneArtwork = GraphZoneArtwork()
    private var zoneMode = false
    private var zoneOverview: GraphVisualOverview?
    private var regionType: String?
    private var preserveNextFitTransition = false
    private var nodeLabels: [String: SKLabelNode] = [:]
    private let nodeHighlights = SKShapeNode()
    private let edgeArtwork = GraphEdgeArtwork()
    private let viewCamera = SKCameraNode()
    private var dark = false
    private var highContrast = false
    private var toolbarZoomRevision = -1
    private var lastToolbarZoom: CGFloat = 1
    private var atlasFitScale: CGFloat = 1
    private var highlightedNodeIds: Set<String> = []
    private var recenterRevision = -1
    private var lastFrame: TimeInterval?
    private var draggingNode: String?

    override init() {
        super.init(size: CGSize(width: 1000, height: 700))
        scaleMode = .resizeFill
        camera = viewCamera
        addChild(viewCamera)
        edgeArtwork.attach(to: self)
        zoneArtwork.attach(to: self)
        nodeHighlights.zPosition = 4
        nodeHighlights.fillColor = .clear
        addChild(nodeHighlights)
    }

    required init?(coder: NSCoder) { return nil }

    func configure(nodes: [GraphNode], edges: [GraphEdge], focus: String?,
                   regionType: String? = nil) {
        guard zoneMode || graphNodes != nodes || graphEdges != edges ||
              focusId != focus || self.regionType != regionType else { return }
        let fromZones = zoneMode
        preserveNextFitTransition = fromZones
        self.regionType = regionType
        zoneMode = false
        zoneArtwork.hide()
        let previousFocus = focusId
        if previousFocus != focus { lastToolbarZoom = 1 }
        graphNodes = nodes
        graphEdges = edges
        nodesById = Dictionary(uniqueKeysWithValues: nodes.map { ($0.entityId, $0) })
        focusId = focus
        if focus == nil {
            layout.configureAtlas(nodes: nodes, edges: edges)
        } else {
            if previousFocus == nil { layout = GraphLayout() }
            layout.configure(nodes: nodes, focus: focus)
            if previousFocus != focus {
                viewCamera.removeAllActions()
                viewCamera.position = .zero
                viewCamera.zRotation = 0
                viewCamera.setScale(1)
            }
        }

        for shape in nodeShapes.values { shape.removeFromParent() }
        atlasArtwork.clear()
        cellInterior.clear()
        for label in nodeLabels.values { label.removeFromParent() }
        nodeShapes.removeAll(keepingCapacity: true)
        nodeLabels.removeAll(keepingCapacity: true)

        let degree = edges.reduce(into: [String: Int]()) { counts, edge in
            counts[edge.subjectId, default: 0] += 1
            counts[edge.objectId, default: 0] += 1
        }
        nodeRadii = Dictionary(uniqueKeysWithValues: nodes.map { node in
            (node.entityId, focus == nil
                ? min(8, 2.5 + sqrt(CGFloat(degree[node.entityId, default: 0])) * 1.4)
                : node.entityId == focus ? 15 : 11)
        })
        if focus == nil {
            if regionType != nil, let interiorTint {
                layout.adopt(GraphCellInterior.arrange(nodes, radii: nodeRadii))
                cellInterior.draw(nodes: nodes, positions: layout.positions, radii: nodeRadii,
                                  tint: interiorTint, dark: dark, in: self)
            } else {
                atlasArtwork.draw(nodes: nodes, positions: layout.positions, radii: nodeRadii,
                                  degree: degree, in: self)
            }
            fitAtlas(animated: fromZones, diveIn: fromZones && regionType != nil)
        } else {
            for node in nodes {
                let shape = SKShapeNode(circleOfRadius: nodeRadii[node.entityId] ?? 11)
                shape.name = "node:" + node.entityId
                shape.zPosition = 2
                addChild(shape)
                nodeShapes[node.entityId] = shape
                let label = makeLabel(for: node)
                nodeLabels[node.entityId] = label
            }
        }

        lastFrame = nil
        toolbarZoomRevision = -1
        updateAppearance(dark: dark, highContrast: highContrast, force: true)
        updatePaths()
        updateVisibility()
    }

    private func makeLabel(for node: GraphNode) -> SKLabelNode {
        let label = SKLabelNode(fontNamed: "Helvetica Neue")
        label.text = String(node.label.prefix(28))
        label.fontSize = 11
        label.horizontalAlignmentMode = .left
        label.verticalAlignmentMode = .center
        label.zPosition = 5
        label.fontColor = .labelColor
        if let point = layout.positions[node.entityId] {
            label.position = CGPoint(x: point.x + 19, y: point.y + 1)
        }
        addChild(label)
        return label
    }

    func configureZones(_ summary: GraphVisualOverview) {
        guard !zoneMode || zoneOverview != summary else { return }
        zoneMode = true
        preserveNextFitTransition = false
        zoneOverview = summary
        regionType = nil
        graphNodes = []
        graphEdges = []
        nodesById.removeAll(keepingCapacity: true)
        nodeRadii.removeAll(keepingCapacity: true)
        for shape in nodeShapes.values { shape.removeFromParent() }
        for label in nodeLabels.values { label.removeFromParent() }
        nodeShapes.removeAll(keepingCapacity: true)
        nodeLabels.removeAll(keepingCapacity: true)
        atlasArtwork.clear()
        edgeArtwork.rebuild(edges: [], positions: [:], focused: false, selected: nil, hovered: nil)
        nodeHighlights.path = nil
        hoveredNodeId = nil
        hoveredEdgeId = nil
        selectedNodeId = nil
        selectedEdgeId = nil
        focusId = nil
        zoneArtwork.show(summary, dark: dark, highContrast: highContrast)
        fitZones()
    }

    override func didChangeSize(_ oldSize: CGSize) {
        super.didChangeSize(oldSize)
        if zoneMode {
            fitZones()
        } else if focusId == nil {
            fitAtlas()
        }
    }

    private func fitZones() {
        let bounds = zoneArtwork.bounds
        guard bounds.width > 0, bounds.height > 0, size.width > 0, size.height > 0 else { return }
        atlasFitScale = max(0.45, bounds.width / (size.width * 0.88),
                                 bounds.height / (size.height * 0.88))
        viewCamera.removeAllActions()
        viewCamera.zRotation = 0
        viewCamera.position = CGPoint(x: bounds.midX, y: bounds.midY)
        viewCamera.setScale(atlasFitScale)
    }

    private func fitAtlas(animated: Bool = false, diveIn: Bool = false) {
        guard !layout.positions.isEmpty, size.width > 0, size.height > 0 else { return }
        let points = Array(layout.positions.values)
        let minX = points.map(\.x).min() ?? 0
        let maxX = points.map(\.x).max() ?? 0
        let minY = points.map(\.y).min() ?? 0
        let maxY = points.map(\.y).max() ?? 0
        atlasFitScale = max(1, (maxX - minX + 100) / (size.width * 0.82),
                               (maxY - minY + 100) / (size.height * 0.82))
        let target = CGPoint(x: (minX + maxX) / 2, y: (minY + maxY) / 2)
        viewCamera.removeAllActions()
        if animated && !reduceMotion {
            // Entering a cell continues the zoom: the camera starts far out and dives in.
            if diveIn {
                viewCamera.position = target
                viewCamera.setScale(atlasFitScale * 3.5)
            }
            let duration = diveIn ? 0.75 : 0.28
            let move = SKAction.move(to: target, duration: duration)
            let scale = SKAction.scale(to: atlasFitScale, duration: duration)
            move.timingMode = .easeOut
            scale.timingMode = .easeOut
            viewCamera.run(.group([move, scale, .rotate(toAngle: 0, duration: duration, shortestUnitArc: true)]))
        } else {
            viewCamera.zRotation = 0
            viewCamera.position = target
            viewCamera.setScale(atlasFitScale)
        }
        updateStrokeWidths()
        updateVisibility()
    }

    func setSelection(node: String?, edge: String?) {
        guard selectedNodeId != node || selectedEdgeId != edge else { return }
        let previousNode = selectedNodeId
        let previousEdge = selectedEdgeId
        selectedNodeId = node
        selectedEdgeId = edge
        refreshNodeStyle(previousNode)
        refreshNodeStyle(node)
        updateHighlightPaths()
        updateVisibility()
        if previousEdge != edge { edgeArtwork.highlight(selected: selectedEdgeId, hovered: hoveredEdgeId) }
    }

    func setToolbarZoom(_ zoom: CGFloat, revision: Int) {
        guard toolbarZoomRevision != revision else { return }
        toolbarZoomRevision = revision
        if focusId == nil && zoom == 1 {
            if preserveNextFitTransition {
                preserveNextFitTransition = false
            } else if zoneMode {
                fitZones()
            } else {
                fitAtlas()
            }
        } else {
            preserveNextFitTransition = false
            let base = focusId == nil ? atlasFitScale : CGFloat(1)
            let relative = lastToolbarZoom > 0 ? zoom / lastToolbarZoom : zoom
            viewCamera.setScale(min(base * 2.5, max(base * 0.12,
                                                     viewCamera.xScale * relative)))
            updateStrokeWidths()
            updateVisibility()
        }
        lastToolbarZoom = zoom
        if regionType != nil, focusId == nil,
           viewCamera.xScale > atlasFitScale * 2.2 {
            didReturnToZones?()
        }
    }

    func setHighlights(_ ids: Set<String>) {
        guard highlightedNodeIds != ids else { return }
        highlightedNodeIds = ids
        updateHighlightPaths()
        updateVisibility()
    }

    func recenter(on id: String?, revision: Int) {
        guard recenterRevision != revision else { return }
        recenterRevision = revision
        guard focusId == nil, let id, let point = layout.positions[id] else { return }
        viewCamera.position = point
        viewCamera.setScale(max(atlasFitScale * 0.35, 0.15))
        updateStrokeWidths()
        updateVisibility()
    }

    func zoom(by factor: CGFloat, at viewPoint: CGPoint) {
        guard factor.isFinite, factor > 0 else { return }
        let before = view == nil ? nil : convertPoint(fromView: viewPoint)
        let base = focusId == nil ? atlasFitScale : CGFloat(1)
        let scale = min(base * 2.5, max(base * 0.12, viewCamera.xScale * factor))
        viewCamera.setScale(scale)
        if let before {
            let after = convertPoint(fromView: viewPoint)
            viewCamera.position.x += before.x - after.x
            viewCamera.position.y += before.y - after.y
        }
        updateStrokeWidths()
        updateVisibility()
        if zoneMode && scale < atlasFitScale * 0.48, let before,
           let regionId = zoneArtwork.nearest(to: before, scale: scale) {
            didSelectZone?(regionId)
        } else if regionType != nil && focusId == nil && scale > atlasFitScale * 2.2 {
            didReturnToZones?()
        }
    }

    func rotate(by radians: CGFloat) {
        guard focusId == nil, radians.isFinite else { return }
        viewCamera.zRotation += radians
        updateVisibility()
    }

    func updateAppearance(dark: Bool, highContrast: Bool, force: Bool = false) {
        guard force || self.dark != dark || self.highContrast != highContrast else { return }
        self.dark = dark
        self.highContrast = highContrast
        backgroundColor = dark ? NSColor(calibratedRed: 0.01, green: 0.035, blue: 0.075, alpha: 1) : .windowBackgroundColor
        if zoneMode, let zoneOverview {
            zoneArtwork.show(zoneOverview, dark: dark, highContrast: highContrast)
            return
        }
        edgeArtwork.applyAppearance(dark: dark, highContrast: highContrast,
                                    cameraScale: viewCamera.xScale)
        nodeHighlights.strokeColor = .controlAccentColor
        for node in graphNodes { refreshNodeStyle(node.entityId) }
        for label in nodeLabels.values { label.fontColor = .labelColor }
        updateHighlightPaths()
    }

    private func refreshNodeStyle(_ id: String?) {
        guard let id, let node = nodesById[id], let shape = nodeShapes[id] else { return }
        let selected = id == selectedNodeId || id == focusId ||
            id == hoveredNodeId || highlightedNodeIds.contains(id)
        shape.fillColor = GraphPalette.color(for: node.entityType, suggested: node.status == "suggested")
        shape.strokeColor = selected ? .controlAccentColor :
            (highContrast ? .labelColor : .separatorColor)
        shape.lineWidth = selected ? 3 : 1.5
        shape.alpha = node.status == "suggested" ? 0.70 : 1
    }

    private func updateHighlightPaths() {
        guard focusId == nil else {
            nodeHighlights.path = nil
            return
        }
        let ids = highlightedNodeIds.union([selectedNodeId, hoveredNodeId].compactMap { $0 })
        let path = CGMutablePath()
        for id in ids {
            guard let point = layout.positions[id], let radius = nodeRadii[id] else { continue }
            let outline = radius + max(2, viewCamera.xScale * 1.1)
            path.addEllipse(in: CGRect(x: point.x - outline, y: point.y - outline,
                                       width: outline * 2, height: outline * 2))
        }
        nodeHighlights.path = path
        nodeHighlights.lineWidth = max(1.5, viewCamera.xScale * 1.5)
    }

    private func updateStrokeWidths() {
        edgeArtwork.setScale(viewCamera.xScale, highContrast: highContrast)
        updateHighlightPaths()
    }

    private func updateVisibility() {
        if zoneMode { return }
        if focusId == nil {
            let detailed = viewCamera.xScale < atlasFitScale * 0.38
            let visibleRadius = hypot(size.width * viewCamera.xScale,
                                      size.height * viewCamera.yScale) * 0.6
            var ids = Array(highlightedNodeIds).sorted()
            for id in [hoveredNodeId, selectedNodeId].compactMap({ $0 }) where !ids.contains(id) {
                ids.append(id)
            }
            if detailed {
                for node in graphNodes where node.mentions >= 8 && ids.count < 72 {
                    guard let point = layout.positions[node.entityId],
                          abs(point.x - viewCamera.position.x) < visibleRadius,
                          abs(point.y - viewCamera.position.y) < visibleRadius else { continue }
                    if !ids.contains(node.entityId) { ids.append(node.entityId) }
                }
            }
            let visible = Set(ids)
            for id in nodeLabels.keys.filter({ !visible.contains($0) }) {
                nodeLabels.removeValue(forKey: id)?.removeFromParent()
            }
            for id in ids {
                guard let node = nodesById[id] else { continue }
                if nodeLabels[id] == nil { nodeLabels[id] = makeLabel(for: node) }
            }
            return
        }
        let expanded = viewCamera.xScale < 0.8
        for node in graphNodes {
            nodeLabels[node.entityId]?.isHidden = !(
                expanded || graphNodes.count <= 24 || node.entityId == focusId || node.entityId == selectedNodeId
            )
        }
    }

    private func updatePaths() {
        edgeArtwork.rebuild(edges: graphEdges, positions: layout.positions, focused: focusId != nil,
                            selected: selectedEdgeId, hovered: hoveredEdgeId)
        for (id, shape) in nodeShapes {
            if let point = layout.positions[id] { shape.position = point }
        }
        for (id, label) in nodeLabels {
            if let point = layout.positions[id] {
                label.position = CGPoint(x: point.x + 19, y: point.y + 1)
            }
        }
    }

    override func update(_ currentTime: TimeInterval) {
        defer { lastFrame = currentTime }
        guard focusId != nil, let lastFrame, currentTime - lastFrame >= 1.0 / 32.0,
              !reduceMotion, draggingNode == nil else { return }
        if layout.advance(nodes: graphNodes, edges: graphEdges, focus: focusId, reducedMotion: false) {
            updatePaths()
        }
    }

    private func hitNode(at location: CGPoint) -> String? {
        if zoneMode { return nil }
        if focusId != nil {
            return nodes(at: location).first(where: { $0.name?.hasPrefix("node:") == true })?.name
                .map { String($0.dropFirst(5)) }
        }
        var closest: String?
        var shortest = CGFloat.infinity
        for node in graphNodes {
            guard let point = layout.positions[node.entityId] else { continue }
            let radius = max(nodeRadii[node.entityId] ?? 3, viewCamera.xScale * 5.5)
            let dx = location.x - point.x
            let dy = location.y - point.y
            let distance = dx * dx + dy * dy
            if distance <= radius * radius && distance < shortest {
                shortest = distance
                closest = node.entityId
            }
        }
        return closest
    }

    override func mouseMoved(with event: NSEvent) {
        let location = event.location(in: self)
        if zoneMode {
            zoneArtwork.hover(zoneArtwork.region(at: location, scale: viewCamera.xScale))
            return
        }
        let node = hitNode(at: location)
        let edge = node == nil ? closestEdge(to: location) : nil
        guard node != hoveredNodeId || edge != hoveredEdgeId else { return }
        let previousNode = hoveredNodeId
        let previousEdge = hoveredEdgeId
        hoveredNodeId = node
        hoveredEdgeId = edge
        refreshNodeStyle(previousNode)
        refreshNodeStyle(node)
        updateHighlightPaths()
        updateVisibility()
        if previousEdge != edge { edgeArtwork.highlight(selected: selectedEdgeId, hovered: hoveredEdgeId) }
    }

    override func mouseDown(with event: NSEvent) {
        guard !event.modifierFlags.contains(.option) else { return }
        if zoneMode {
            if let regionId = zoneArtwork.region(at: event.location(in: self), scale: viewCamera.xScale) {
                didSelectZone?(regionId)
            }
            return
        }
        if let id = hitNode(at: event.location(in: self)) {
            if focusId != nil { draggingNode = id }
            didSelectNode?(id)
            didSelectEdge?(nil)
        } else if let edge = closestEdge(to: event.location(in: self)) {
            didSelectEdge?(edge)
            didSelectNode?(nil)
        } else {
            didSelectEdge?(nil)
            didSelectNode?(nil)
        }
    }

    override func mouseDragged(with event: NSEvent) {
        if focusId == nil && event.modifierFlags.contains(.option) {
            rotate(by: -event.deltaX * 0.006)
        } else if let draggingNode {
            layout.move(draggingNode, to: event.location(in: self))
            updatePaths()
        } else {
            let dx = -event.deltaX * viewCamera.xScale
            let dy = event.deltaY * viewCamera.yScale
            let angle = viewCamera.zRotation
            viewCamera.position.x += dx * cos(angle) - dy * sin(angle)
            viewCamera.position.y += dx * sin(angle) + dy * cos(angle)
            updateVisibility()
        }
    }

    override func mouseUp(with event: NSEvent) {
        draggingNode = nil
    }

    private func closestEdge(to point: CGPoint) -> String? {
        var bestDistance = 9 * viewCamera.xScale
        var best: String?
        for edge in graphEdges {
            guard let a = layout.positions[edge.subjectId],
                  let b = layout.positions[edge.objectId] else { continue }
            let dx = b.x - a.x
            let dy = b.y - a.y
            let lengthSquared = max(1, dx * dx + dy * dy)
            let t = min(1, max(0, ((point.x - a.x) * dx + (point.y - a.y) * dy) / lengthSquared))
            let projected = CGPoint(x: a.x + t * dx, y: a.y + t * dy)
            let distance = hypot(point.x - projected.x, point.y - projected.y)
            if distance < bestDistance {
                bestDistance = distance
                best = edge.relationId
            }
        }
        return best
    }
}
