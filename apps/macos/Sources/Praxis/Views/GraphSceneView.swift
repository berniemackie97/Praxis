import AppKit
import SpriteKit
import SwiftUI

/// SpriteKit draws the current projection; SwiftUI remains the accessible control surface.
@MainActor
struct GraphSceneView: NSViewRepresentable {
    let nodes: [GraphNode]
    let edges: [GraphEdge]
    let focusId: String?
    let highlightedNodeIds: Set<String>
    let recenterNodeId: String?
    let recenterRevision: Int
    @Binding var selectedNodeId: String?
    @Binding var selectedEdgeId: String?
    let reduceMotion: Bool
    let active: Bool
    let highContrast: Bool
    let colorScheme: ColorScheme
    let zoom: CGFloat
    let zoomRevision: Int

    func makeCoordinator() -> Coordinator {
        Coordinator(selectedNodeId: $selectedNodeId, selectedEdgeId: $selectedEdgeId)
    }

    func makeNSView(context: Context) -> SKView {
        let view = GraphInputView()
        view.ignoresSiblingOrder = true
        view.shouldCullNonVisibleNodes = true
        view.preferredFramesPerSecond = 60
        view.presentScene(context.coordinator.scene)
        return view
    }

    func updateNSView(_ view: SKView, context: Context) {
        let scene = context.coordinator.scene
        view.window?.acceptsMouseMovedEvents = true
        scene.configure(nodes: nodes, edges: edges, focus: focusId)
        scene.updateAppearance(dark: colorScheme == .dark, highContrast: highContrast)
        scene.setSelection(node: selectedNodeId, edge: selectedEdgeId)
        scene.setHighlights(highlightedNodeIds)
        scene.reduceMotion = reduceMotion
        scene.setToolbarZoom(zoom, revision: zoomRevision)
        scene.recenter(on: recenterNodeId, revision: recenterRevision)
        view.isPaused = !active
    }

    static func dismantleNSView(_ view: SKView, coordinator: Coordinator) {
        view.isPaused = true
        view.presentScene(nil)
    }

    @MainActor
    final class Coordinator {
        let scene = GraphScene()

        init(selectedNodeId: Binding<String?>, selectedEdgeId: Binding<String?>) {
            scene.didSelectNode = { selectedNodeId.wrappedValue = $0 }
            scene.didSelectEdge = { selectedEdgeId.wrappedValue = $0 }
        }
    }
}


@MainActor
private final class GraphInputView: SKView {
    override func scrollWheel(with event: NSEvent) {
        guard let graph = scene as? GraphScene else {
            super.scrollWheel(with: event)
            return
        }
        let delta = max(-80, min(80, event.scrollingDeltaY))
        let sensitivity: CGFloat = event.hasPreciseScrollingDeltas ? 0.012 : 0.12
        graph.zoom(by: exp(-delta * sensitivity),
                   at: convert(event.locationInWindow, from: nil))
    }

    override func magnify(with event: NSEvent) {
        guard let graph = scene as? GraphScene else {
            super.magnify(with: event)
            return
        }
        graph.zoom(by: exp(-CGFloat(event.magnification)),
                   at: convert(event.locationInWindow, from: nil))
    }

    override func rotate(with event: NSEvent) {
        guard let graph = scene as? GraphScene else {
            super.rotate(with: event)
            return
        }
        graph.rotate(by: -CGFloat(event.rotation) * .pi / 180)
    }
}
