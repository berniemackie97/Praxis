import AppKit
import SpriteKit

@MainActor
final class GraphScene: SKScene {
    var didSelectNode: ((String?) -> Void)?
    var didSelectEdge: ((String?) -> Void)?
    var reduceMotion = false
    private var graphNodes: [GraphNode] = []
    private var graphEdges: [GraphEdge] = []
    private var layout = GraphLayout()
    private var focusId: String?
    private var selectedNodeId: String?
    private var selectedEdgeId: String?
    private var hoveredNodeId: String?
    private var hoveredEdgeId: String?
    private var nodeShapes: [String: SKShapeNode] = [:]
    private var nodeLabels: [String: SKLabelNode] = [:]
    private let edgeLines = SKShapeNode()
    private let edgeArrows = SKShapeNode()
    private let highlightedEdge = SKShapeNode()
    private let edgeLabel = SKLabelNode(fontNamed: "Helvetica Neue")
    private let viewCamera = SKCameraNode()
    private var dark = false
    private var highContrast = false
    private var toolbarZoomRevision = -1
    private var lastFrame: TimeInterval?
    private var draggingNode: String?

    override init() {
        super.init(size: CGSize(width: 1000, height: 700))
        scaleMode = .resizeFill
        viewCamera.position = .zero
        camera = viewCamera
        addChild(viewCamera)
        edgeLines.zPosition = -4
        edgeArrows.zPosition = -3
        highlightedEdge.zPosition = -2
        for layer in [edgeLines, edgeArrows, highlightedEdge] {
            layer.lineWidth = 1
            addChild(layer)
        }
        edgeLabel.fontSize = 11
        edgeLabel.zPosition = 10
        addChild(edgeLabel)
        edgeLabel.isHidden = true
    }

    required init?(coder: NSCoder) { return nil }

    func configure(nodes: [GraphNode], edges: [GraphEdge], focus: String?) {
        guard graphNodes != nodes || graphEdges != edges || focusId != focus else { return }
        graphNodes = nodes
        graphEdges = edges
        focusId = focus
        layout.configure(nodes: nodes, focus: focus)
        for shape in nodeShapes.values { shape.removeFromParent() }
        for label in nodeLabels.values { label.removeFromParent() }
        nodeShapes.removeAll(keepingCapacity: true)
        nodeLabels.removeAll(keepingCapacity: true)

        for node in nodes {
            let shape = SKShapeNode(circleOfRadius: node.entityId == focus ? 15 : 11)
            shape.name = "node:" + node.entityId
            shape.lineWidth = 2
            shape.zPosition = 2
            addChild(shape)
            nodeShapes[node.entityId] = shape

            let label = SKLabelNode(fontNamed: "Helvetica Neue")
            label.text = String(node.label.prefix(28))
            label.fontSize = 11
            label.horizontalAlignmentMode = .left
            label.verticalAlignmentMode = .center
            label.zPosition = 3
            addChild(label)
            nodeLabels[node.entityId] = label
        }
        lastFrame = nil
        updateAppearance(dark: dark, highContrast: highContrast)
        updatePaths()
    }

    func setSelection(node: String?, edge: String?) {
        guard selectedNodeId != node || selectedEdgeId != edge else { return }
        selectedNodeId = node
        selectedEdgeId = edge
        updateAppearance(dark: dark, highContrast: highContrast)
    }

    func setToolbarZoom(_ zoom: CGFloat, revision: Int) {
        guard toolbarZoomRevision != revision else { return }
        toolbarZoomRevision = revision
        viewCamera.setScale(zoom)
        updateVisibility()
    }

    func updateAppearance(dark: Bool, highContrast: Bool) {
        self.dark = dark
        self.highContrast = highContrast
        backgroundColor = dark ? NSColor(calibratedWhite: 0.08, alpha: 1) : .windowBackgroundColor
        edgeLines.strokeColor = (dark ? NSColor.lightGray : .secondaryLabelColor)
            .withAlphaComponent(highContrast ? 0.48 : 0.20)
        edgeArrows.strokeColor = edgeLines.strokeColor
        highlightedEdge.strokeColor = .controlAccentColor
        highlightedEdge.lineWidth = highContrast ? 3 : 2
        edgeLabel.fontColor = .labelColor
        for node in graphNodes {
            guard let shape = nodeShapes[node.entityId] else { continue }
            let selected = node.entityId == selectedNodeId || node.entityId == focusId ||
                node.entityId == hoveredNodeId
            shape.fillColor = Self.color(for: node.entityType, suggested: node.status == "suggested")
            shape.strokeColor = selected ? .controlAccentColor :
                (highContrast ? .labelColor : .separatorColor)
            shape.lineWidth = selected ? 3 : 1.5
            shape.alpha = node.status == "suggested" ? 0.70 : 1
            nodeLabels[node.entityId]?.fontColor = .labelColor
        }
        updateVisibility()
        updatePaths()
    }

    private static func color(for type: String?, suggested: Bool) -> NSColor {
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
        default: return .secondaryLabelColor
        }
    }

    private func updateVisibility() {
        let expanded = viewCamera.xScale < 0.8
        for node in graphNodes {
            nodeLabels[node.entityId]?.isHidden = !(
                expanded || graphNodes.count <= 24 || node.entityId == focusId || node.entityId == selectedNodeId
            )
        }
    }

    private func updatePaths() {
        let points = layout.positions
        let lines = CGMutablePath()
        let arrows = CGMutablePath()
        let selection = CGMutablePath()
        edgeLabel.isHidden = true
        for edge in graphEdges {
            guard let a = points[edge.subjectId], let b = points[edge.objectId] else { continue }
            let dx = b.x - a.x
            let dy = b.y - a.y
            let distance = max(1, hypot(dx, dy))
            let ux = dx / distance
            let uy = dy / distance
            let start = CGPoint(x: a.x + ux * 15, y: a.y + uy * 15)
            let end = CGPoint(x: b.x - ux * 15, y: b.y - uy * 15)
            let path = edge.relationId == selectedEdgeId || edge.relationId == hoveredEdgeId
                ? selection : lines
            path.move(to: start)
            path.addLine(to: end)
            let wing = highContrast ? CGFloat(7) : 5
            arrows.move(to: end)
            arrows.addLine(to: CGPoint(x: end.x - ux * wing - uy * wing,
                                       y: end.y - uy * wing + ux * wing))
            arrows.move(to: end)
            arrows.addLine(to: CGPoint(x: end.x - ux * wing + uy * wing,
                                       y: end.y - uy * wing - ux * wing))
            if edge.relationId == selectedEdgeId {
                edgeLabel.text = KnowledgeVocabulary.label(edge.predicate)
                edgeLabel.position = CGPoint(x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 + 12)
                edgeLabel.isHidden = false
            }
        }
        edgeLines.path = lines
        edgeArrows.path = arrows
        highlightedEdge.path = selection
        for (id, shape) in nodeShapes {
            if let location = points[id] {
                shape.position = location
                nodeLabels[id]?.position = CGPoint(x: location.x + 19, y: location.y + 1)
            }
        }
    }

    override func update(_ currentTime: TimeInterval) {
        defer { lastFrame = currentTime }
        guard let lastFrame, currentTime - lastFrame >= 1.0 / 32.0,
              !reduceMotion, draggingNode == nil else { return }
        if layout.advance(nodes: graphNodes, edges: graphEdges, focus: focusId, reducedMotion: false) {
            updatePaths()
        }
    }

    override func mouseMoved(with event: NSEvent) {
        let location = event.location(in: self)
        let node = nodes(at: location).first(where: { $0.name?.hasPrefix("node:") == true })?.name
            .map { String($0.dropFirst(5)) }
        let edge = node == nil ? closestEdge(to: location) : nil
        guard node != hoveredNodeId || edge != hoveredEdgeId else { return }
        hoveredNodeId = node
        hoveredEdgeId = edge
        updateAppearance(dark: dark, highContrast: highContrast)
    }

    override func mouseDown(with event: NSEvent) {
        let location = event.location(in: self)
        if let hit = nodes(at: location).first(where: { $0.name?.hasPrefix("node:") == true }),
           let id = hit.name.map({ String($0.dropFirst(5)) }) {
            draggingNode = id
            didSelectNode?(id)
            didSelectEdge?(nil)
            return
        }
        if let edge = closestEdge(to: location) {
            didSelectEdge?(edge)
            didSelectNode?(nil)
        } else {
            didSelectEdge?(nil)
            didSelectNode?(nil)
        }
    }

    override func mouseDragged(with event: NSEvent) {
        if let draggingNode {
            layout.move(draggingNode, to: event.location(in: self))
            updatePaths()
        } else {
            viewCamera.position.x -= event.deltaX * viewCamera.xScale
            viewCamera.position.y += event.deltaY * viewCamera.yScale
        }
    }

    override func mouseUp(with event: NSEvent) {
        draggingNode = nil
    }

    override func scrollWheel(with event: NSEvent) {
        let scale = min(2.5, max(0.35, viewCamera.xScale * exp(event.scrollingDeltaY * 0.008)))
        viewCamera.setScale(scale)
        updateVisibility()
    }

    private func closestEdge(to point: CGPoint) -> String? {
        var bestDistance: CGFloat = 10
        var best: String?
        for edge in graphEdges {
            guard let a = layout.positions[edge.subjectId], let b = layout.positions[edge.objectId] else { continue }
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
