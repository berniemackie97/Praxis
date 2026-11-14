import AppKit
import SpriteKit
import SwiftUI

/// SpriteKit draws the current projection; SwiftUI remains the accessible control surface.
@MainActor
struct GraphSceneView: NSViewRepresentable {
    let nodes: [GraphNode]
    let edges: [GraphEdge]
    let focusId: String?
    @Binding var selectedNodeId: String?
    @Binding var selectedEdgeId: String?
    let reduceMotion: Bool
    let highContrast: Bool
    let colorScheme: ColorScheme
    let zoom: CGFloat
    let zoomRevision: Int

    func makeCoordinator() -> Coordinator {
        Coordinator(selectedNodeId: $selectedNodeId, selectedEdgeId: $selectedEdgeId)
    }

    func makeNSView(context: Context) -> SKView {
        let view = SKView()
        view.ignoresSiblingOrder = true
        view.shouldCullNonVisibleNodes = true
        view.preferredFramesPerSecond = 30
        view.presentScene(context.coordinator.scene)
        return view
    }

    func updateNSView(_ view: SKView, context: Context) {
        let scene = context.coordinator.scene
        view.window?.acceptsMouseMovedEvents = true
        scene.configure(nodes: nodes, edges: edges, focus: focusId)
        scene.updateAppearance(dark: colorScheme == .dark, highContrast: highContrast)
        scene.setSelection(node: selectedNodeId, edge: selectedEdgeId)
        scene.reduceMotion = reduceMotion
        scene.setToolbarZoom(zoom, revision: zoomRevision)
        view.isPaused = false
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

