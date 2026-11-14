import SpriteKit
import SwiftUI

struct SpriteGraphView: NSViewRepresentable {
    let fixture: GraphFixture
    let animated: Bool
    @Binding var selection: String?

    func makeCoordinator() -> Coordinator { Coordinator(fixture: fixture, selection: $selection) }

    func makeNSView(context: Context) -> SKView {
        let view = SKView()
        view.ignoresSiblingOrder = true
        view.presentScene(context.coordinator.scene)
        return view
    }

    func updateNSView(_ view: SKView, context: Context) {
        context.coordinator.scene.selectedId = selection
        context.coordinator.scene.motionEnabled = animated
        if !animated { context.coordinator.scene.updatePositions(at: 0) }
        view.isPaused = !animated
    }

    final class Coordinator {
        let scene: GraphScene

        init(fixture: GraphFixture, selection: Binding<String?>) {
            scene = GraphScene(fixture: fixture)
            scene.onSelect = { selection.wrappedValue = $0 }
            scene.scaleMode = .resizeFill
            scene.backgroundColor = .windowBackgroundColor
        }
    }

    final class GraphScene: SKScene {
        private let fixture: GraphFixture
        var motionEnabled = true
        var onSelect: ((String?) -> Void)?
        var selectedId: String? {
            didSet {
                guard oldValue != selectedId else { return }
                for (index, circle) in circles.enumerated() {
                    circle.fillColor = fixture.nodes[index].id == selectedId ? .systemOrange : .controlAccentColor
                }
            }
        }
        private var circles: [SKShapeNode] = []
        private var labels: [SKLabelNode] = []
        private let lines = SKShapeNode()
        private let indices: [String: Int]

        init(fixture: GraphFixture) {
            self.fixture = fixture
            self.indices = Dictionary(uniqueKeysWithValues: fixture.nodes.enumerated().map { ($0.element.id, $0.offset) })
            super.init(size: CGSize(width: 900, height: 600))
            lines.strokeColor = .secondaryLabelColor
            lines.lineWidth = 0.75
            lines.zPosition = -1
            addChild(lines)
            for node in fixture.nodes {
                let circle = SKShapeNode(circleOfRadius: 4)
                circle.fillColor = .controlAccentColor
                circle.strokeColor = .clear
                circle.name = node.id
                addChild(circle)
                circles.append(circle)
                if fixture.nodes.count <= 200 {
                    let label = SKLabelNode(text: node.label)
                    label.fontSize = 9
                    label.fontColor = .labelColor
                    label.horizontalAlignmentMode = .left
                    label.verticalAlignmentMode = .center
                    addChild(label)
                    labels.append(label)
                }
            }
            updatePositions(at: 0)
        }

        required init?(coder: NSCoder) { return nil }

        override func mouseDown(with event: NSEvent) {
            let location = event.location(in: self)
            guard let nearest = circles.enumerated().min(by: {
                hypot($0.element.position.x - location.x, $0.element.position.y - location.y) <
                hypot($1.element.position.x - location.x, $1.element.position.y - location.y)
            }), hypot(nearest.element.position.x - location.x, nearest.element.position.y - location.y) <= 12 else {
                onSelect?(nil)
                return
            }
            onSelect?(fixture.nodes[nearest.offset].id)
        }

        override func update(_ currentTime: TimeInterval) {
            updatePositions(at: currentTime)
        }

        override func didChangeSize(_ oldSize: CGSize) {
            updatePositions(at: 0)
        }

        func updatePositions(at time: TimeInterval) {
            let positions = GraphPositions.layout(fixture.nodes, in: size, at: time, animated: motionEnabled)
            for (index, point) in positions.enumerated() {
                let position = CGPoint(x: point.x, y: size.height - point.y)
                circles[index].position = position
                if index < labels.count {
                    labels[index].position = CGPoint(x: position.x + 8, y: position.y)
                }
            }
            let path = CGMutablePath()
            for edge in fixture.edges {
                guard let source = indices[edge.sourceId], let target = indices[edge.targetId] else { continue }
                path.move(to: CGPoint(x: positions[source].x, y: size.height - positions[source].y))
                path.addLine(to: CGPoint(x: positions[target].x, y: size.height - positions[target].y))
            }
            lines.path = path
        }
    }
}
