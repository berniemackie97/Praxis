import Foundation
import MetalKit
import simd

private struct NeuralNebulaUniforms {
    var viewProjection: simd_float4x4
    var time: Float
    var opacity: Float
    var pointScale: Float
    var padding: Float = 0
}

@MainActor
final class NeuralNebulaRenderer: NSObject, @preconcurrency MTKViewDelegate {
    var onEnterZone: ((String) -> Void)?
    var onReturnToZones: (() -> Void)?
    var onSelectNode: ((String) -> Void)?
    var onSelectEdge: ((String) -> Void)?
    var onFrame: (([String: CGPoint], String?) -> Void)?

    private struct Batch {
        var buffer: (any MTLBuffer)?
        var count = 0
    }

    private let device: any MTLDevice
    private let queue: any MTLCommandQueue
    private let pointsPipeline: any MTLRenderPipelineState
    private let linesPipeline: any MTLRenderPipelineState
    private let depthState: any MTLDepthStencilState
    private weak var view: MTKView?
    private var zonePoints = Batch()
    private var zoneLines = Batch()
    private var detailPoints = Batch()
    private var detailLines = Batch()
    private var overview: GraphZoneOverview?
    private var graphNodes: [GraphNode] = []
    private var graphEdges: [GraphEdge] = []
    private var regionType: String?
    private var focusId: String?
    private var selectedNodeId: String?
    private var selectedEdgeId: String?
    private var placement = NeuralNebulaPlacement(zoneCenters: [:], zoneSizes: [:], nodeCenters: [:])
    private var camera = NeuralNebulaCamera()
    private var detailOpacity: Float = 0
    private var reduceMotion = false
    private var highContrast = false
    private var lastDrawTime: CFTimeInterval?
    private var zoomRevision = -1
    private var lastToolbarZoom: CGFloat = 1

    init?(view: MTKView) {
        guard let device = MTLCreateSystemDefaultDevice(),
              let queue = device.makeCommandQueue(),
              let library = device.makeDefaultLibrary(),
              let vertex = library.makeFunction(name: "nebulaVertex"),
              let point = library.makeFunction(name: "nebulaPoint"),
              let line = library.makeFunction(name: "nebulaLine") else { return nil }

        func pipeline(_ fragment: any MTLFunction) -> (any MTLRenderPipelineState)? {
            let descriptor = MTLRenderPipelineDescriptor()
            descriptor.vertexFunction = vertex
            descriptor.fragmentFunction = fragment
            descriptor.colorAttachments[0].pixelFormat = .bgra8Unorm
            descriptor.depthAttachmentPixelFormat = .depth32Float
            let attachment = descriptor.colorAttachments[0]!
            attachment.isBlendingEnabled = true
            attachment.rgbBlendOperation = .add
            attachment.alphaBlendOperation = .add
            attachment.sourceRGBBlendFactor = .sourceAlpha
            attachment.destinationRGBBlendFactor = .oneMinusSourceAlpha
            attachment.sourceAlphaBlendFactor = .one
            attachment.destinationAlphaBlendFactor = .oneMinusSourceAlpha
            return try? device.makeRenderPipelineState(descriptor: descriptor)
        }
        guard let pointsPipeline = pipeline(point), let linesPipeline = pipeline(line) else { return nil }
        let depthDescriptor = MTLDepthStencilDescriptor()
        depthDescriptor.depthCompareFunction = .lessEqual
        depthDescriptor.isDepthWriteEnabled = false
        guard let depthState = device.makeDepthStencilState(descriptor: depthDescriptor) else { return nil }
        self.device = device
        self.queue = queue
        self.pointsPipeline = pointsPipeline
        self.linesPipeline = linesPipeline
        self.depthState = depthState
        self.view = view
        super.init()
        view.device = device
        view.colorPixelFormat = .bgra8Unorm
        view.depthStencilPixelFormat = .depth32Float
        view.clearColor = MTLClearColor(red: 0.008, green: 0.015, blue: 0.039, alpha: 1)
        view.preferredFramesPerSecond = 60
        view.isPaused = false
    }

    func update(overview: GraphZoneOverview?, nodes: [GraphNode], edges: [GraphEdge],
                regionType: String?, focusId: String?,
                selectedNodeId: String?, selectedEdgeId: String?,
                reduceMotion: Bool, highContrast: Bool, active: Bool) {
        self.reduceMotion = reduceMotion
        let changed = self.overview != overview || graphNodes != nodes || graphEdges != edges ||
            self.regionType != regionType || self.focusId != focusId ||
            self.selectedNodeId != selectedNodeId || self.selectedEdgeId != selectedEdgeId ||
            self.highContrast != highContrast
        self.highContrast = highContrast
        if changed {
            self.overview = overview
            graphNodes = nodes
            graphEdges = edges
            self.regionType = regionType
            self.focusId = focusId
            self.selectedNodeId = selectedNodeId
            self.selectedEdgeId = selectedEdgeId
            rebuild()
        }
        view?.enableSetNeedsDisplay = reduceMotion
        view?.isPaused = !active
        if active { view?.setNeedsDisplay(view?.bounds ?? .zero) }
    }

    private func buffer(_ vertices: [NeuralNebulaVertex]) -> Batch {
        guard !vertices.isEmpty else { return Batch() }
        let value = vertices.withUnsafeBytes { data in
            device.makeBuffer(bytes: data.baseAddress!, length: data.count, options: .storageModeShared)
        }
        return Batch(buffer: value, count: value == nil ? 0 : vertices.count)
    }

    private func rebuild() {
        guard let overview else {
            placement = NeuralNebulaPlacement(zoneCenters: [:], zoneSizes: [:], nodeCenters: [:])
            zonePoints = Batch()
            zoneLines = Batch()
            detailPoints = Batch()
            detailLines = Batch()
            detailOpacity = 0
            camera.desiredCenter = .zero
            camera.desiredDistance = 590
            return
        }
        let visibleRegion = regionType ?? (focusId.flatMap { id in
            graphNodes.first(where: { $0.entityId == id })?.entityType
        })
        placement = NeuralNebulaLayout.place(zones: overview, nodes: graphNodes,
                                              edges: graphEdges, regionType: visibleRegion)
        let geometry = NeuralNebulaGeometry.build(overview: overview, placement: placement,
                                                  nodes: graphNodes, edges: graphEdges,
                                                  selectedNodeId: selectedNodeId,
                                                  highContrast: highContrast)
        zonePoints = buffer(geometry.zonePoints)
        zoneLines = buffer(geometry.zoneLines)
        detailPoints = buffer(geometry.detailPoints)
        detailLines = buffer(geometry.detailLines)
        if regionType != nil || focusId != nil {
            camera.desiredCenter = focusId.flatMap { placement.nodeCenters[$0] } ??
                visibleRegion.flatMap { placement.zoneCenters[$0] } ?? .zero
            camera.desiredDistance = focusId == nil ? 157 : 79
        } else {
            camera.desiredCenter = .zero
            camera.desiredDistance = 590
        }
        if reduceMotion {
            camera.center = camera.desiredCenter
            camera.distance = camera.desiredDistance
            detailOpacity = (regionType != nil || focusId != nil) && !graphNodes.isEmpty ? 1 : 0
        }
    }

    func orbit(dx: Float, dy: Float) { camera.orbit(dx: dx, dy: dy) }

    func pan(dx: Float, dy: Float) { camera.pan(dx: dx, dy: dy) }

    func zoom(by factor: Float, at point: CGPoint?) {
        camera.desiredDistance = max(48, min(1_400, camera.desiredDistance * factor))
        if regionType == nil, focusId == nil, let point, let zone = zone(at: point) {
            if let anchor = placement.zoneCenters[zone] {
                camera.desiredCenter += (anchor - camera.desiredCenter) * 0.16
            }
            if camera.desiredDistance < 325 { onEnterZone?(zone) }
        } else if (regionType != nil || focusId != nil) && camera.desiredDistance > 415 {
            onReturnToZones?()
        }
    }

    func setToolbarZoom(_ zoom: CGFloat, revision: Int) {
        guard revision != zoomRevision else { return }
        let ratio = Float(zoom / lastToolbarZoom)
        lastToolbarZoom = zoom
        zoomRevision = revision
        if zoom == 1 {
            camera.desiredCenter = focusId.flatMap { placement.nodeCenters[$0] } ??
                regionType.flatMap { placement.zoneCenters[$0] } ?? .zero
            camera.desiredDistance = focusId != nil ? 79 : regionType == nil ? 590 : 157
        } else {
            self.zoom(by: ratio, at: nil)
        }
    }

    func click(at point: CGPoint) {
        if regionType != nil || focusId != nil {
            if let node = nearestNode(to: point) { onSelectNode?(node); return }
            if let edge = nearestEdge(to: point) { onSelectEdge?(edge); return }
        } else if let type = zone(at: point) {
            onEnterZone?(type)
        }
    }

    private func projected(_ point: SIMD3<Float>) -> CGPoint? {
        guard let view, view.bounds.width > 0, view.bounds.height > 0 else { return nil }
        let clip = camera.projection(aspect: Float(view.bounds.width / view.bounds.height)) *
            SIMD4<Float>(point, 1)
        guard clip.w > 0 else { return nil }
        return CGPoint(x: CGFloat((clip.x / clip.w + 1) * 0.5) * view.bounds.width,
                       y: CGFloat((clip.y / clip.w + 1) * 0.5) * view.bounds.height)
    }

    private func zone(at point: CGPoint) -> String? {
        placement.zoneCenters.compactMap { entry -> (String, CGFloat)? in
            guard let position = projected(entry.value) else { return nil }
            let camera.distance = hypot(position.x - point.x, position.y - point.y)
            return camera.distance < 42 ? (entry.key, camera.distance) : nil
        }.min { $0.1 < $1.1 }?.0
    }

    private func nearestNode(to point: CGPoint) -> String? {
        placement.nodeCenters.compactMap { entry -> (String, CGFloat)? in
            guard let position = projected(entry.value) else { return nil }
            let camera.distance = hypot(position.x - point.x, position.y - point.y)
            return camera.distance < 14 ? (entry.key, camera.distance) : nil
        }.min { $0.1 < $1.1 }?.0
    }

    private func nearestEdge(to point: CGPoint) -> String? {
        graphEdges.compactMap { edge -> (String, CGFloat)? in
            guard let a = placement.nodeCenters[edge.subjectId].flatMap(projected),
                  let b = placement.nodeCenters[edge.objectId].flatMap(projected) else { return nil }
            let delta = CGPoint(x: b.x - a.x, y: b.y - a.y)
            let squared = delta.x * delta.x + delta.y * delta.y
            guard squared > 1 else { return nil }
            let projection = max(0, min(1, ((point.x - a.x) * delta.x +
                                          (point.y - a.y) * delta.y) / squared))
            let camera.distance = hypot(point.x - a.x - delta.x * projection,
                                 point.y - a.y - delta.y * projection)
            return camera.distance < 7 ? (edge.relationId, camera.distance) : nil
        }.min { $0.1 < $1.1 }?.0
    }

    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {
        view.setNeedsDisplay(view.bounds)
    }

    func draw(in view: MTKView) {
        let now = CACurrentMediaTime()
        let elapsed = Float(min(0.05, now - (lastDrawTime ?? now)))
        lastDrawTime = now
        let weight = reduceMotion ? Float(1) : 1 - exp(-elapsed * 5.3)
        camera.settle(weight: weight)
        let desiredOpacity: Float = (regionType != nil || focusId != nil) && !graphNodes.isEmpty ? 1 : 0
        detailOpacity += (desiredOpacity - detailOpacity) * weight
        guard let pass = view.currentRenderPassDescriptor,
              let drawable = view.currentDrawable,
              let command = queue.makeCommandBuffer(),
              let encoder = command.makeRenderCommandEncoder(descriptor: pass) else { return }
        encoder.setDepthStencilState(depthState)
        let aspect = max(0.1, Float(view.drawableSize.width / max(1, view.drawableSize.height)))
        var uniform = NeuralNebulaUniforms(viewProjection: camera.projection(aspect: aspect),
                                            time: reduceMotion ? 0 : Float(now),
                                            opacity: 1, pointScale: Float(view.drawableSize.height))
        let baseOpacity = 1 - detailOpacity * 0.82
        draw(zoneLines, lines: true, opacity: baseOpacity, uniform: &uniform, encoder: encoder)
        draw(detailLines, lines: true, opacity: detailOpacity, uniform: &uniform, encoder: encoder)
        draw(zonePoints, lines: false, opacity: baseOpacity, uniform: &uniform, encoder: encoder)
        draw(detailPoints, lines: false, opacity: detailOpacity, uniform: &uniform, encoder: encoder)
        encoder.endEncoding()
        command.present(drawable)
        command.commit()
        if let onFrame {
            let positions = placement.zoneCenters.reduce(into: [String: CGPoint]()) { result, entry in
                if let point = projected(entry.value) { result[entry.key] = point }
            }
            onFrame(positions, regionType)
        }
    }

    private func draw(_ batch: Batch, lines: Bool, opacity: Float,
                      uniform: inout NeuralNebulaUniforms, encoder: any MTLRenderCommandEncoder) {
        guard let buffer = batch.buffer, batch.count > 0, opacity > 0.003 else { return }
        uniform.opacity = opacity
        encoder.setRenderPipelineState(lines ? linesPipeline : pointsPipeline)
        encoder.setVertexBuffer(buffer, offset: 0, index: 0)
        encoder.setVertexBytes(&uniform, length: MemoryLayout<NeuralNebulaUniforms>.stride, index: 1)
        encoder.drawPrimitives(type: lines ? .line : .point, vertexStart: 0, vertexCount: batch.count)
    }

}
