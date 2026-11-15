import AppKit
import MetalKit
import SwiftUI

/// Living Cortex: the same region map and members as the other styles, drawn as an anatomical
/// brain whose territories are the owner's knowledge regions.
@MainActor
struct LivingCortexView: NSViewRepresentable {
    let regions: [GraphKnowledgeRegion]
    let map: GraphRegionMap?
    let links: [GraphVisualLink]
    let openRegionId: String?
    let nodes: [GraphNode]
    let edges: [GraphEdge]
    let enterRegion: (String) -> Void
    let returnToBrain: () -> Void
    @Binding var selectedNodeId: String?
    @Binding var selectedEdgeId: String?
    let reduceMotion: Bool
    let active: Bool
    let highContrast: Bool
    let fitRevision: Int
    let onTier: (Int) -> Void

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> LivingCortexInputView {
        let view = LivingCortexInputView(frame: .zero, device: nil)
        if let renderer = LivingCortexRenderer(view: view) {
            context.coordinator.renderer = renderer
            view.cortexRenderer = renderer
            renderer.onFrame = { [weak view, coordinator = context.coordinator] labels in
                view?.show(labels)
                if coordinator.tier != labels.tier {
                    coordinator.tier = labels.tier
                    coordinator.onTier?(labels.tier)
                }
            }
            view.delegate = renderer
        } else {
            view.showFailure()
        }
        return view
    }

    func updateNSView(_ view: LivingCortexInputView, context: Context) {
        guard let renderer = context.coordinator.renderer else { return }
        view.territoryText = Dictionary(uniqueKeysWithValues: regions.map { region in
            (region.regionId, (region.label.uppercased(),
                               region.forming ? "forming · \(region.pendingSources) queued" : "\(region.entities.formatted()) entities",
                               BrainPalette.rgb(for: region, in: map)))
        })
        view.neuronText = Dictionary(uniqueKeysWithValues: nodes.map { ($0.entityId, $0.label) })
        context.coordinator.onTier = onTier
        renderer.onEnterRegion = enterRegion
        renderer.onReturn = returnToBrain
        renderer.onSelectNode = { selectedNodeId = $0; selectedEdgeId = nil }
        renderer.onSelectEdge = { selectedEdgeId = $0; selectedNodeId = nil }
        renderer.update(regions: regions, map: map, links: links, openId: openRegionId, nodes: nodes, edges: edges,
                        selectedNodeId: selectedNodeId, selectedEdgeId: selectedEdgeId,
                        reduceMotion: reduceMotion, highContrast: highContrast, active: active)
        if context.coordinator.fitRevision != fitRevision {
            context.coordinator.fitRevision = fitRevision
            renderer.fit()
        }
    }

    static func dismantleNSView(_ view: LivingCortexInputView, coordinator: Coordinator) {
        view.isPaused = true
        view.delegate = nil
        view.cortexRenderer = nil
        coordinator.renderer = nil
    }

    @MainActor
    final class Coordinator {
        var renderer: LivingCortexRenderer?
        var fitRevision = 0
        var tier = -1
        var onTier: ((Int) -> Void)?
    }
}

@MainActor
final class LivingCortexInputView: MTKView {
    weak var cortexRenderer: LivingCortexRenderer?
    var territoryText: [String: (String, String, SIMD3<Float>)] = [:]
    var neuronText: [String: String] = [:]
    private var territoryLabels: [String: CortexLabel] = [:]
    private var neuronLabels: [String: CortexLabel] = [:]

    override var acceptsFirstResponder: Bool { true }

    func showFailure() {
        let label = NSTextField(labelWithString: "Living Cortex requires an available Metal renderer.")
        label.textColor = .white
        label.alignment = .center
        label.frame = CGRect(x: 0, y: 0, width: 420, height: 30)
        addSubview(label)
    }

    func show(_ labels: LivingCortexRenderer.Labels) {
        sync(&territoryLabels, positions: labels.territories, width: 190) { label, id in
            guard let (title, detail, color) = territoryText[id] else { return }
            let text = NSMutableAttributedString(string: title + "\n", attributes: [
                .font: NSFont.systemFont(ofSize: 11, weight: .semibold),
                .foregroundColor: NSColor(calibratedRed: CGFloat(color.x), green: CGFloat(color.y), blue: CGFloat(color.z), alpha: 1)
            ])
            text.append(NSAttributedString(string: detail, attributes: [
                .font: NSFont.monospacedSystemFont(ofSize: 10, weight: .regular),
                .foregroundColor: NSColor(white: 0.92, alpha: 0.75)
            ]))
            label.attributedStringValue = text
        }
        for (id, label) in territoryLabels {
            if let point = labels.territories[id] { label.frame.origin = CGPoint(x: point.x - 95, y: point.y - 44) }
        }
        sync(&neuronLabels, positions: labels.neurons, width: 240) { label, id in
            let name = neuronText[id] ?? ""
            label.attributedStringValue = NSAttributedString(string: name.count > 34 ? String(name.prefix(32)) + "…" : name, attributes: [
                .font: NSFont.monospacedSystemFont(ofSize: 10.5, weight: .regular),
                .foregroundColor: NSColor(white: 0.97, alpha: 0.9)
            ])
            label.alignment = .left
        }
        for (id, label) in neuronLabels {
            if let point = labels.neurons[id] { label.frame.origin = CGPoint(x: point.x + 9, y: point.y - 8) }
        }
    }

    private func sync(_ store: inout [String: CortexLabel], positions: [String: CGPoint], width: CGFloat,
                      configure: (CortexLabel, String) -> Void) {
        for id in store.keys where positions[id] == nil {
            store.removeValue(forKey: id)?.removeFromSuperview()
        }
        for id in positions.keys where store[id] == nil {
            let label = CortexLabel(frame: CGRect(x: 0, y: 0, width: width, height: 30))
            label.isBezeled = false
            label.drawsBackground = false
            label.isEditable = false
            label.isSelectable = false
            label.alignment = .center
            label.maximumNumberOfLines = 2
            configure(label, id)
            addSubview(label)
            store[id] = label
        }
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        if event.clickCount == 2 {
            cortexRenderer?.zoom(by: 0.5, at: convert(event.locationInWindow, from: nil))
        } else {
            cortexRenderer?.click(at: convert(event.locationInWindow, from: nil))
        }
    }

    override func mouseDragged(with event: NSEvent) {
        if event.modifierFlags.contains(.option) || event.modifierFlags.contains(.shift) {
            cortexRenderer?.pan(dx: Float(event.deltaX), dy: Float(event.deltaY))
        } else {
            cortexRenderer?.orbit(dx: Float(event.deltaX), dy: Float(event.deltaY))
        }
    }

    override func rightMouseDragged(with event: NSEvent) {
        cortexRenderer?.pan(dx: Float(event.deltaX), dy: Float(event.deltaY))
    }

    override func scrollWheel(with event: NSEvent) {
        let delta = max(-80, min(80, event.scrollingDeltaY))
        let sensitivity: Float = event.hasPreciseScrollingDeltas ? 0.012 : 0.12
        cortexRenderer?.zoom(by: exp(-Float(delta) * sensitivity), at: convert(event.locationInWindow, from: nil))
    }

    override func magnify(with event: NSEvent) {
        cortexRenderer?.zoom(by: exp(-Float(event.magnification)), at: convert(event.locationInWindow, from: nil))
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { cortexRenderer?.onReturn?() } else { super.keyDown(with: event) }
    }
}

@MainActor
private final class CortexLabel: NSTextField {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}
