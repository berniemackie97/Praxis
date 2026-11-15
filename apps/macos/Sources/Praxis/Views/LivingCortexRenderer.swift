import CoreGraphics
import Foundation
import MetalKit
import QuartzCore
import simd

private struct CortexUniforms {
    var viewProjection: simd_float4x4
    var time: Float
    var opacity: Float
    var pointScale: Float
    var pulse: Float
}

/// One persistent Metal scene for Living Cortex: the ornamental brain, its knowledge territories
/// and evidence pathways, and an entered territory's neurons and synapses. Levels crossfade by
/// camera distance; nothing is unmounted between tiers.
@MainActor
final class LivingCortexRenderer: NSObject, MTKViewDelegate {
    struct Labels {
        var territories: [String: CGPoint] = [:]
        var neurons: [String: CGPoint] = [:]
        /// T0 whole brain, T1 territory, T2 neural network, T3 neuron and evidence.
        var tier = 0
    }

    var onEnterRegion: ((String) -> Void)?
    var onReturn: (() -> Void)?
    var onSelectNode: ((String) -> Void)?
    var onSelectEdge: ((String) -> Void)?
    var onFrame: ((Labels) -> Void)?

    private struct Batch {
        var buffer: (any MTLBuffer)?
        var count = 0
    }

    private let device: any MTLDevice
    private let queue: any MTLCommandQueue
    private let pointsPipeline: any MTLRenderPipelineState
    private let linesPipeline: any MTLRenderPipelineState
    private weak var view: MTKView?
    private let shell = CortexShell.build()
    private var shellPoints = Batch()
    private var shellLines = Batch()
    private var glows = Batch()
    private var pathways = Batch()
    private var neuronPoints = Batch()
    private var synapseLines = Batch()
    private var dendriteLines = Batch()
    private var smoke = Batch()
    private(set) var territories: [CortexTerritory] = []
    private var links: [GraphVisualLink] = []
    private var nodes: [GraphNode] = []
    private var edges: [GraphEdge] = []
    private var neuronAt: [String: SIMD3<Float>] = [:]
    private var openId: String?
    private var selectedNodeId: String?
    private var selectedEdgeId: String?
    private var camera = LivingCortexCamera()
    private var neuronOpacity: Float = 0
    private var reduceMotion = false
    private var highContrast = false
    private var lastDrawTime: CFTimeInterval?
    private let started = CACurrentMediaTime()

    init?(view: MTKView?) {
        guard let device = MTLCreateSystemDefaultDevice(),
              let queue = device.makeCommandQueue(),
              let library = device.makeDefaultLibrary(),
              let vertex = library.makeFunction(name: "cortexVertex"),
              let point = library.makeFunction(name: "cortexPoint"),
              let line = library.makeFunction(name: "cortexLine") else { return nil }
        func pipeline(_ fragment: any MTLFunction) -> (any MTLRenderPipelineState)? {
            let descriptor = MTLRenderPipelineDescriptor()
            descriptor.vertexFunction = vertex
            descriptor.fragmentFunction = fragment
            descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
            let attachment = descriptor.colorAttachments[0]!
            attachment.isBlendingEnabled = true
            attachment.rgbBlendOperation = .add
            attachment.alphaBlendOperation = .add
            attachment.sourceRGBBlendFactor = .sourceAlpha
            attachment.destinationRGBBlendFactor = .one
            attachment.sourceAlphaBlendFactor = .one
            attachment.destinationAlphaBlendFactor = .one
            return try? device.makeRenderPipelineState(descriptor: descriptor)
        }
        guard let pointsPipeline = pipeline(point), let linesPipeline = pipeline(line) else { return nil }
        self.device = device
        self.queue = queue
        self.pointsPipeline = pointsPipeline
        self.linesPipeline = linesPipeline
        self.view = view
        super.init()
        smoke = buffer(Self.smokeVertices())
        if let view {
            view.device = device
            view.colorPixelFormat = .bgra8Unorm
            view.clearColor = MTLClearColor(red: 0.012, green: 0.006, blue: 0.010, alpha: 1)
            view.preferredFramesPerSecond = 60
            view.isPaused = false
        }
    }

    // MARK: Data

    func update(regions: [GraphKnowledgeRegion], map: GraphRegionMap?, links: [GraphVisualLink],
                openId: String?, nodes: [GraphNode], edges: [GraphEdge],
                selectedNodeId: String?, selectedEdgeId: String?,
                reduceMotion: Bool, highContrast: Bool, active: Bool) {
        self.reduceMotion = reduceMotion
        let territories = LivingCortexLayout.territories(regions, map: map)
        let regionsChanged = territories != self.territories || links != self.links || highContrast != self.highContrast
        let openChanged = openId != self.openId
        let membersChanged = nodes != self.nodes || edges != self.edges
        let selectionChanged = selectedNodeId != self.selectedNodeId || selectedEdgeId != self.selectedEdgeId
        self.territories = territories
        self.links = links
        self.highContrast = highContrast
        self.openId = openId
        self.nodes = nodes
        self.edges = edges
        self.selectedNodeId = selectedNodeId
        self.selectedEdgeId = selectedEdgeId
        if regionsChanged {
            shellPoints = buffer(LivingCortexLayout.shellPoints(shell, territories: territories, highContrast: highContrast))
            shellLines = buffer(LivingCortexLayout.shellLines(shell, territories: territories, highContrast: highContrast))
            pathways = buffer(LivingCortexLayout.pathways(links, territories: territories, highContrast: highContrast))
        }
        if regionsChanged || openChanged {
            glows = buffer(LivingCortexLayout.territoryGlows(territories, openId: openId))
        }
        if regionsChanged || openChanged || membersChanged || selectionChanged {
            rebuildNeurons()
        }
        if openChanged {
            if let open = openTerritory {
                camera.face(open.position, distance: LivingCortexCamera.territory)
            } else {
                camera.reset()
            }
        }
        if selectionChanged, let selectedNodeId, let point = neuronAt[selectedNodeId] {
            camera.desiredCenter = point
            camera.desiredDistance = min(camera.desiredDistance, LivingCortexCamera.neuron * 1.6)
        }
        if reduceMotion { settleImmediately() }
        view?.isPaused = !active
        view?.enableSetNeedsDisplay = reduceMotion
        if active { view?.setNeedsDisplay(view?.bounds ?? .zero) }
    }

    private var openTerritory: CortexTerritory? { territories.first { $0.id == openId } }

    private func rebuildNeurons() {
        guard let open = openTerritory, !nodes.isEmpty else {
            neuronAt = [:]
            neuronPoints = Batch()
            synapseLines = Batch()
            dendriteLines = Batch()
            return
        }
        neuronAt = LivingCortexLayout.neuronPositions(nodes, territory: open)
        neuronPoints = buffer(LivingCortexLayout.neurons(nodes, positions: neuronAt, color: open.color, selectedId: selectedNodeId))
        synapseLines = buffer(LivingCortexLayout.synapses(edges, positions: neuronAt, color: open.color,
                                                          selectedNodeId: selectedNodeId, selectedEdgeId: selectedEdgeId))
        dendriteLines = buffer(LivingCortexLayout.dendrites(nodes, positions: neuronAt, color: open.color))
    }

    private func buffer(_ vertices: [CortexVertex]) -> Batch {
        guard !vertices.isEmpty else { return Batch() }
        let value = vertices.withUnsafeBufferPointer { data in
            device.makeBuffer(bytes: data.baseAddress!, length: data.count * MemoryLayout<CortexVertex>.stride,
                              options: .storageModeShared)
        }
        return Batch(buffer: value, count: value == nil ? 0 : vertices.count)
    }

    private static func smokeVertices() -> [CortexVertex] {
        var random = CortexRandom(seed: 7)
        return (0..<9).map { _ in
            CortexVertex(position: SIMD3<Float>(random.next() * 360 - 180, random.next() * 260 - 140, -160 - random.next() * 120),
                         color: [0.45, 0.06, 0.05, 0.16], size: 260 + random.next() * 240, phase: random.next())
        }
    }

    // MARK: Input

    func orbit(dx: Float, dy: Float) {
        if openId == nil { camera.orbit(dx: dx, dy: dy) } else { camera.pan(dx: dx, dy: dy) }
        invalidate()
    }

    func pan(dx: Float, dy: Float) {
        camera.pan(dx: dx, dy: dy)
        invalidate()
    }

    func zoom(by factor: Float, at point: CGPoint?) {
        camera.desiredDistance = max(22, min(700, camera.desiredDistance * factor))
        if openId == nil, factor < 1, let point, let territory = territory(at: point) {
            if let anchor = territories.first(where: { $0.id == territory })?.position {
                camera.desiredCenter += (anchor - camera.desiredCenter) * 0.18
            }
            if camera.desiredDistance < LivingCortexCamera.wholeBrain * 0.55 { onEnterRegion?(territory) }
        } else if openId != nil, camera.desiredDistance > LivingCortexCamera.wholeBrain * 0.8 {
            onReturn?()
        }
        invalidate()
    }

    func fit() {
        if let open = openTerritory { camera.face(open.position, distance: LivingCortexCamera.territory) } else { camera.reset() }
        invalidate()
    }

    func click(at point: CGPoint) {
        if openId != nil {
            if let node = nearestNeuron(to: point) { onSelectNode?(node); return }
            if let edge = nearestSynapse(to: point) { onSelectEdge?(edge); return }
        }
        if let territory = territory(at: point), territory != openId { onEnterRegion?(territory) }
    }

    private func invalidate() {
        if reduceMotion {
            settleImmediately()
            view?.setNeedsDisplay(view?.bounds ?? .zero)
        }
    }

    private func settleImmediately() {
        camera.settle(weight: 1)
        neuronOpacity = openId != nil && !nodes.isEmpty ? 1 : 0
    }

    // MARK: Projection and hit testing

    private var sway: Float {
        guard !reduceMotion, openId == nil else { return 0 }
        return 0.95 * sin(Float(CACurrentMediaTime() - started) * 0.11)
    }

    private func projected(_ point: SIMD3<Float>, size: CGSize) -> CGPoint? {
        guard size.width > 0, size.height > 0 else { return nil }
        let clip = camera.projection(aspect: Float(size.width / size.height), sway: sway) * SIMD4<Float>(point, 1)
        guard clip.w > 0 else { return nil }
        return CGPoint(x: CGFloat((clip.x / clip.w + 1) * 0.5) * size.width,
                       y: CGFloat((clip.y / clip.w + 1) * 0.5) * size.height)
    }

    private var bounds: CGSize { view?.bounds.size ?? .zero }

    private func territory(at point: CGPoint) -> String? {
        territories.compactMap { territory -> (String, CGFloat)? in
            guard let position = projected(territory.position, size: bounds) else { return nil }
            let distance = hypot(position.x - point.x, position.y - point.y)
            let reach = max(28, CGFloat(territory.radius) * 260 * CGFloat(LivingCortexCamera.wholeBrain / camera.distance))
            return distance < reach ? (territory.id, distance) : nil
        }.min { $0.1 < $1.1 }?.0
    }

    private func nearestNeuron(to point: CGPoint) -> String? {
        neuronAt.compactMap { entry -> (String, CGFloat)? in
            guard let position = projected(entry.value, size: bounds) else { return nil }
            let distance = hypot(position.x - point.x, position.y - point.y)
            return distance < 16 ? (entry.key, distance) : nil
        }.min { $0.1 < $1.1 }?.0
    }

    private func nearestSynapse(to point: CGPoint) -> String? {
        edges.compactMap { edge -> (String, CGFloat)? in
            guard let a = neuronAt[edge.subjectId].flatMap({ projected($0, size: bounds) }),
                  let b = neuronAt[edge.objectId].flatMap({ projected($0, size: bounds) }) else { return nil }
            let delta = CGPoint(x: b.x - a.x, y: b.y - a.y)
            let squared = delta.x * delta.x + delta.y * delta.y
            guard squared > 1 else { return nil }
            let t = max(0, min(1, ((point.x - a.x) * delta.x + (point.y - a.y) * delta.y) / squared))
            let distance = hypot(point.x - a.x - delta.x * t, point.y - a.y - delta.y * t)
            return distance < 7 ? (edge.relationId, distance) : nil
        }.min { $0.1 < $1.1 }?.0
    }

    // MARK: Drawing

    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {
        view.setNeedsDisplay(view.bounds)
    }

    func draw(in view: MTKView) {
        let now = CACurrentMediaTime()
        let elapsed = Float(min(0.05, now - (lastDrawTime ?? now)))
        lastDrawTime = now
        advance(weight: reduceMotion ? 1 : 1 - exp(-elapsed * 2.4))
        guard let pass = view.currentRenderPassDescriptor, let drawable = view.currentDrawable,
              let command = queue.makeCommandBuffer(),
              let encoder = command.makeRenderCommandEncoder(descriptor: pass) else { return }
        encode(encoder, size: view.drawableSize, time: reduceMotion ? 0 : Float(now - started))
        encoder.endEncoding()
        command.present(drawable)
        command.commit()
        if let onFrame { onFrame(labels(size: view.bounds.size)) }
    }

    private func advance(weight: Float) {
        camera.settle(weight: weight)
        let target: Float = openId != nil && !nodes.isEmpty ? 1 : 0
        // Neurons fade more slowly than the camera moves, so the territory resolves as it is approached.
        neuronOpacity += (target - neuronOpacity) * weight * 0.7
    }

    private func encode(_ encoder: any MTLRenderCommandEncoder, size: CGSize, time: Float) {
        let aspect = max(0.1, Float(size.width / max(1, size.height)))
        var uniform = CortexUniforms(viewProjection: camera.projection(aspect: aspect, sway: sway), time: time,
                                     opacity: 1, pointScale: Float(size.height), pulse: reduceMotion ? 0 : 1)
        // Zooming in fades the cortex shell so the territory's neural network reads clearly.
        let closeness = max(0, min(1, (LivingCortexCamera.wholeBrain * 0.62 - camera.distance) / (LivingCortexCamera.wholeBrain * 0.45)))
        let shellOpacity = 1 - 0.82 * closeness
        draw(smoke, lines: false, opacity: reduceMotion ? 0.6 : 1, uniform: &uniform, encoder: encoder)
        draw(shellLines, lines: true, opacity: shellOpacity, uniform: &uniform, encoder: encoder)
        draw(shellPoints, lines: false, opacity: shellOpacity, uniform: &uniform, encoder: encoder)
        draw(pathways, lines: true, opacity: 1 - 0.6 * neuronOpacity, uniform: &uniform, encoder: encoder)
        draw(glows, lines: false, opacity: 1 - 0.55 * neuronOpacity, uniform: &uniform, encoder: encoder)
        let detail = max(0, min(1, (LivingCortexCamera.territory * 0.9 - camera.distance) / (LivingCortexCamera.territory * 0.5)))
        draw(dendriteLines, lines: true, opacity: neuronOpacity * detail, uniform: &uniform, encoder: encoder)
        draw(synapseLines, lines: true, opacity: neuronOpacity, uniform: &uniform, encoder: encoder)
        draw(neuronPoints, lines: false, opacity: neuronOpacity, uniform: &uniform, encoder: encoder)
    }

    private func draw(_ batch: Batch, lines: Bool, opacity: Float, uniform: inout CortexUniforms,
                      encoder: any MTLRenderCommandEncoder) {
        guard let buffer = batch.buffer, batch.count > 0, opacity > 0.003 else { return }
        uniform.opacity = opacity
        encoder.setRenderPipelineState(lines ? linesPipeline : pointsPipeline)
        encoder.setVertexBuffer(buffer, offset: 0, index: 0)
        encoder.setVertexBytes(&uniform, length: MemoryLayout<CortexUniforms>.stride, index: 1)
        encoder.setFragmentBytes(&uniform, length: MemoryLayout<CortexUniforms>.stride, index: 1)
        encoder.drawPrimitives(type: lines ? .line : .point, vertexStart: 0, vertexCount: batch.count)
    }

    private func labels(size: CGSize) -> Labels {
        var result = Labels()
        result.tier = selectedNodeId != nil && openId != nil ? 3
            : openId == nil ? 0 : camera.distance < LivingCortexCamera.territory * 0.7 ? 2 : 1
        let showTerritories = camera.distance > LivingCortexCamera.territory * 0.75
        if showTerritories {
            for territory in territories {
                if let point = projected(territory.position, size: size) { result.territories[territory.id] = point }
            }
        }
        if neuronOpacity > 0.5 {
            let shown = camera.distance < LivingCortexCamera.territory * 0.7 ? nodes : Array(nodes.prefix(12))
            for node in shown {
                if let position = neuronAt[node.entityId], let point = projected(position, size: size) {
                    result.neurons[node.entityId] = point
                }
            }
        }
        return result
    }

    /// Renders the current scene into an image without a window, after settling the camera.
    /// Used by tests and screenshot capture, where Metal views cannot be captured from layers.
    func snapshot(size: CGSize, time: Float = 2) -> CGImage? {
        settleImmediately()
        let width = Int(size.width), height = Int(size.height)
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: width, height: height, mipmapped: false)
        descriptor.usage = [.renderTarget, .shaderRead]
        descriptor.storageMode = .shared
        guard let texture = device.makeTexture(descriptor: descriptor), let command = queue.makeCommandBuffer() else { return nil }
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].texture = texture
        pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].storeAction = .store
        pass.colorAttachments[0].clearColor = MTLClearColor(red: 0.012, green: 0.006, blue: 0.010, alpha: 1)
        guard let encoder = command.makeRenderCommandEncoder(descriptor: pass) else { return nil }
        let wasReduced = reduceMotion
        reduceMotion = true
        encode(encoder, size: size, time: time)
        reduceMotion = wasReduced
        encoder.endEncoding()
        command.commit()
        command.waitUntilCompleted()
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        texture.getBytes(&bytes, bytesPerRow: width * 4, from: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0)
        guard let provider = CGDataProvider(data: Data(bytes) as CFData) else { return nil }
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                       space: CGColorSpaceCreateDeviceRGB(),
                       bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
    }
}
