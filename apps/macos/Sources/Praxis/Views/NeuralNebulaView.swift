import AppKit
import MetalKit
import SwiftUI

/// The same authorized graph projection as Cellular Atlas, rendered in persistent 3D space.
@MainActor
struct NeuralNebulaView: NSViewRepresentable {
    let zones: GraphZoneOverview?
    let nodes: [GraphNode]
    let edges: [GraphEdge]
    let regionType: String?
    let focusId: String?
    let expandZone: (String) -> Void
    let returnToZones: () -> Void
    @Binding var selectedNodeId: String?
    @Binding var selectedEdgeId: String?
    let reduceMotion: Bool
    let active: Bool
    let highContrast: Bool
    let zoom: CGFloat
    let zoomRevision: Int

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> NeuralNebulaInputView {
        let view = NeuralNebulaInputView(frame: .zero, device: nil)
        if let renderer = NeuralNebulaRenderer(view: view) {
            context.coordinator.renderer = renderer
            view.graphRenderer = renderer
            renderer.onFrame = { [weak view] labels, region in
                view?.showLabels(labels, selectedRegion: region)
            }
            view.delegate = renderer
        } else {
            view.showFailure()
        }
        return view
    }

    func updateNSView(_ view: NeuralNebulaInputView, context: Context) {
        guard let renderer = context.coordinator.renderer else { return }
        renderer.onEnterZone = expandZone
        renderer.onReturnToZones = returnToZones
        renderer.onSelectNode = { selectedNodeId = $0; selectedEdgeId = nil }
        renderer.onSelectEdge = { selectedEdgeId = $0; selectedNodeId = nil }
        renderer.update(overview: zones, nodes: nodes, edges: edges,
                        regionType: regionType, focusId: focusId,
                        selectedNodeId: selectedNodeId, selectedEdgeId: selectedEdgeId,
                        reduceMotion: reduceMotion, highContrast: highContrast, active: active)
        renderer.setToolbarZoom(zoom, revision: zoomRevision)
    }

    static func dismantleNSView(_ view: NeuralNebulaInputView, coordinator: Coordinator) {
        view.isPaused = true
        view.delegate = nil
        view.graphRenderer = nil
        coordinator.renderer = nil
    }

    @MainActor
    final class Coordinator {
        var renderer: NeuralNebulaRenderer?
    }
}

@MainActor
final class NeuralNebulaInputView: MTKView {
    weak var graphRenderer: NeuralNebulaRenderer?
    private var labels: [String: NebulaZoneLabel] = [:]

    override var acceptsFirstResponder: Bool { true }

    func showFailure() {
        let label = NSTextField(labelWithString: "Neural Nebula requires an available Metal renderer.")
        label.textColor = .white
        label.font = .systemFont(ofSize: 13, weight: .medium)
        label.alignment = .center
        label.frame = CGRect(x: 0, y: 0, width: 440, height: 30)
        addSubview(label)
    }

    func showLabels(_ positions: [String: CGPoint], selectedRegion: String?) {
        for type in labels.keys.filter({ positions[$0] == nil }) {
            labels.removeValue(forKey: type)?.removeFromSuperview()
        }
        for (type, point) in positions {
            let label: NebulaZoneLabel
            if let existing = labels[type] {
                label = existing
            } else {
                label = NebulaZoneLabel(labelWithString: type.replacingOccurrences(of: "_", with: " ").capitalized)
                label.isBezeled = false
                label.drawsBackground = false
                label.isEditable = false
                label.isSelectable = false
                label.font = .systemFont(ofSize: 11, weight: .medium)
                label.textColor = NSColor(calibratedRed: 0.82, green: 0.91, blue: 1, alpha: 0.91)
                label.alignment = .center
                label.frame.size = CGSize(width: 150, height: 19)
                addSubview(label)
                labels[type] = label
            }
            label.isHidden = selectedRegion != nil && selectedRegion != type
            label.frame.origin = CGPoint(x: point.x - 75, y: point.y + 27)
        }
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        graphRenderer?.click(at: convert(event.locationInWindow, from: nil))
    }

    override func mouseDragged(with event: NSEvent) {
        if event.modifierFlags.contains(.option) {
            graphRenderer?.pan(dx: Float(event.deltaX), dy: Float(event.deltaY))
        } else {
            graphRenderer?.orbit(dx: Float(event.deltaX), dy: Float(event.deltaY))
        }
    }

    override func rightMouseDragged(with event: NSEvent) {
        graphRenderer?.pan(dx: Float(event.deltaX), dy: Float(event.deltaY))
    }

    override func scrollWheel(with event: NSEvent) {
        let delta = max(-80, min(80, event.scrollingDeltaY))
        let sensitivity: Float = event.hasPreciseScrollingDeltas ? 0.012 : 0.12
        graphRenderer?.zoom(by: exp(-Float(delta) * sensitivity),
                            at: convert(event.locationInWindow, from: nil))
    }

    override func magnify(with event: NSEvent) {
        graphRenderer?.zoom(by: exp(-Float(event.magnification)),
                            at: convert(event.locationInWindow, from: nil))
    }

    override func rotate(with event: NSEvent) {
        graphRenderer?.orbit(dx: Float(event.rotation) * 2, dy: 0)
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            graphRenderer?.onReturnToZones?()
        } else {
            super.keyDown(with: event)
        }
    }
}

@MainActor
private final class NebulaZoneLabel: NSTextField {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}
