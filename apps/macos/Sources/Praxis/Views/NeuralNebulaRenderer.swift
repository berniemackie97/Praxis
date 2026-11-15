import Foundation
import MetalKit
import simd

struct NeuralNebulaVertex {
    var position: SIMD3<Float>
    var color: SIMD4<Float>
    var size: Float
}

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
    private var center = SIMD3<Float>.zero
    private var desiredCenter = SIMD3<Float>.zero
    private var distance: Float = 590
    private var desiredDistance: Float = 590
    private var yaw: Float = 0.28
    private var pitch: Float = 0.15
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
            desiredCenter = .zero
            desiredDistance = 590
            return
        }
        placement = NeuralNebulaLayout.place(zones: overview, nodes: graphNodes,
                                              edges: graphEdges, regionType: regionType)
        var points: [NeuralNebulaVertex] = []
        var links: [NeuralNebulaVertex] = []
        let ordered = overview.zones.sorted { $0.entityType < $1.entityType }
        for (index, zone) in ordered.enumerated() {
            guard let center = placement.zoneCenters[zone.entityType] else { continue }
            let color = NeuralNebulaLayout.color(for: zone.entityType)
            let size = placement.zoneSizes[zone.entityType] ?? 12
            points.append(NeuralNebulaVertex(position: center, color: color, size: size))
            let satellites = min(72, max(10, Int(log2(Double(zone.count) + 1) * 10)))
            for number in 0..<satellites {
                let radial = sqrt(Float(number + 1) / Float(satellites))
                let angle = Float(number) * 2.399963
                let latitude = 1 - 2 * ((Float(number) + 0.5) / Float(satellites))
                let plane = sqrt(max(0, 1 - latitude * latitude))
                let displacement = SIMD3<Float>(cos(angle) * plane, latitude,
                                                 sin(angle) * plane) * (size * 1.45 * radial)
                points.append(NeuralNebulaVertex(position: center + displacement,
                    color: SIMD4<Float>(color.x, color.y, color.z, highContrast ? 0.52 : 0.25),
                    size: highContrast ? 2.6 : 1.7))
            }
            _ = index
        }
        for number in 0..<220 {
            let latitude = 1 - 2 * ((Float(number) + 0.5) / 220)
            let angle = Float(number) * 2.399963
            let radial = sqrt(max(0, 1 - latitude * latitude))
            let position = SIMD3<Float>(cos(angle) * radial, latitude, sin(angle) * radial) * 197
            points.append(NeuralNebulaVertex(position: position,
                color: SIMD4<Float>(0.30, 0.55, 0.84, highContrast ? 0.21 : 0.10), size: 1.3))
        }

        for relation in overview.links {
            guard let a = placement.zoneCenters[relation.subjectType],
                  let b = placement.zoneCenters[relation.objectType],
                  relation.subjectType != relation.objectType else { continue }
            if relation.confirmedCount > 0 {
                appendLink(from: a, to: b, color: SIMD4<Float>(0.31, 0.77, 1, 0.47), into: &links)
            }
            if relation.suggestedCount > 0 {
                appendLink(from: a, to: b, color: SIMD4<Float>(0.98, 0.69, 0.31, 0.45), into: &links)
            }
            if relation.contradictedCount > 0 {
                appendLink(from: a, to: b, color: SIMD4<Float>(1, 0.37, 0.44, 0.53), into: &links)
            }
        }

        var localPoints: [NeuralNebulaVertex] = []
        var localLinks: [NeuralNebulaVertex] = []
        for node in graphNodes {
            guard let position = placement.nodeCenters[node.entityId] else { continue }
            var color = NeuralNebulaLayout.color(for: node.entityType ?? "other")
            if node.entityId == selectedNodeId { color = SIMD4<Float>(1, 1, 1, 1) }
            localPoints.append(NeuralNebulaVertex(position: position, color: color,
                 size: node.entityId == selectedNodeId ? 10 : (highContrast ? 6.5 : 4.7)))
        }
        for edge in graphEdges {
            guard let a = placement.nodeCenters[edge.subjectId],
                  let b = placement.nodeCenters[edge.objectId] else { continue }
            let color: SIMD4<Float>
            switch edge.evidenceStatus {
            case "suggested": color = SIMD4<Float>(1, 0.71, 0.33, 0.49)
            case "contradicted": color = SIMD4<Float>(1, 0.41, 0.48, 0.58)
            default: color = SIMD4<Float>(0.55, 0.81, 1, highContrast ? 0.73 : 0.40)
            }
            appendLink(from: a, to: b, color: color, into: &localLinks)
        }
        zonePoints = buffer(points)
        zoneLines = buffer(links)
        detailPoints = buffer(localPoints)
        detailLines = buffer(localLinks)
        if let regionType, let point = placement.zoneCenters[regionType] {
            desiredCenter = focusId.flatMap { placement.nodeCenters[$0] } ?? point
            desiredDistance = focusId == nil ? 157 : 79
        } else {
            desiredCenter = .zero
            desiredDistance = 590
        }
        if reduceMotion {
            center = desiredCenter
            distance = desiredDistance
            detailOpacity = regionType != nil && !graphNodes.isEmpty ? 1 : 0
        }
    }

    private func appendLink(from a: SIMD3<Float>, to b: SIMD3<Float>,
                            color: SIMD4<Float>, into vertices: inout [NeuralNebulaVertex]) {
        guard simd_length_squared(b - a) > 0.001 else { return }
        vertices.append(NeuralNebulaVertex(position: a, color: color, size: 1))
        vertices.append(NeuralNebulaVertex(position: b, color: color, size: 1))
    }

    func orbit(dx: Float, dy: Float) {
        yaw += dx * 0.006
        pitch = max(-1.38, min(1.38, pitch - dy * 0.006))
    }

    func pan(dx: Float, dy: Float) {
        let rate = max(0.1, distance / 600)
        desiredCenter += SIMD3<Float>(-dx * rate, dy * rate, 0)
    }

    func zoom(by factor: Float, at point: CGPoint?) {
        desiredDistance = max(48, min(1_400, desiredDistance * factor))
        if regionType == nil, desiredDistance < 325,
           let point, let zone = zone(at: point) {
            onEnterZone?(zone)
        } else if regionType != nil && desiredDistance > 415 {
            onReturnToZones?()
        }
    }

    func setToolbarZoom(_ zoom: CGFloat, revision: Int) {
        guard revision != zoomRevision else { return }
        let ratio = Float(zoom / lastToolbarZoom)
        lastToolbarZoom = zoom
        zoomRevision = revision
        if zoom == 1 {
            desiredCenter = regionType.flatMap { placement.zoneCenters[$0] } ?? .zero
            desiredDistance = regionType == nil ? 590 : 157
        } else {
            self.zoom(by: ratio, at: nil)
        }
    }

    func click(at point: CGPoint) {
        if regionType != nil {
            if let node = nearestNode(to: point) { onSelectNode?(node); return }
            if let edge = nearestEdge(to: point) { onSelectEdge?(edge); return }
        } else if let type = zone(at: point) {
            onEnterZone?(type)
        }
    }

    private func projected(_ point: SIMD3<Float>) -> CGPoint? {
        guard let view, view.bounds.width > 0, view.bounds.height > 0 else { return nil }
        let clip = projection(aspect: Float(view.bounds.width / view.bounds.height)) *
            SIMD4<Float>(point, 1)
        guard clip.w > 0 else { return nil }
        return CGPoint(x: CGFloat((clip.x / clip.w + 1) * 0.5) * view.bounds.width,
                       y: CGFloat((clip.y / clip.w + 1) * 0.5) * view.bounds.height)
    }

    private func zone(at point: CGPoint) -> String? {
        placement.zoneCenters.compactMap { entry -> (String, CGFloat)? in
            guard let position = projected(entry.value) else { return nil }
            let distance = hypot(position.x - point.x, position.y - point.y)
            return distance < 42 ? (entry.key, distance) : nil
        }.min { $0.1 < $1.1 }?.0
    }

    private func nearestNode(to point: CGPoint) -> String? {
        placement.nodeCenters.compactMap { entry -> (String, CGFloat)? in
            guard let position = projected(entry.value) else { return nil }
            let distance = hypot(position.x - point.x, position.y - point.y)
            return distance < 14 ? (entry.key, distance) : nil
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
            let distance = hypot(point.x - a.x - delta.x * projection,
                                 point.y - a.y - delta.y * projection)
            return distance < 7 ? (edge.relationId, distance) : nil
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
        center += (desiredCenter - center) * weight
        distance += (desiredDistance - distance) * weight
        let desiredOpacity: Float = regionType != nil && !graphNodes.isEmpty ? 1 : 0
        detailOpacity += (desiredOpacity - detailOpacity) * weight
        guard let pass = view.currentRenderPassDescriptor,
              let drawable = view.currentDrawable,
              let command = queue.makeCommandBuffer(),
              let encoder = command.makeRenderCommandEncoder(descriptor: pass) else { return }
        encoder.setDepthStencilState(depthState)
        let aspect = max(0.1, Float(view.drawableSize.width / max(1, view.drawableSize.height)))
        var uniform = NeuralNebulaUniforms(viewProjection: projection(aspect: aspect),
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

    private func projection(aspect: Float) -> simd_float4x4 {
        let eye = center + SIMD3<Float>(sin(yaw) * cos(pitch), sin(pitch),
                                         cos(yaw) * cos(pitch)) * distance
        let direction = simd_normalize(center - eye)
        let right = simd_normalize(simd_cross(direction, SIMD3<Float>(0, 1, 0)))
        let up = simd_cross(right, direction)
        let view = simd_float4x4(columns: (
            SIMD4<Float>(right.x, up.x, -direction.x, 0),
            SIMD4<Float>(right.y, up.y, -direction.y, 0),
            SIMD4<Float>(right.z, up.z, -direction.z, 0),
            SIMD4<Float>(-simd_dot(right, eye), -simd_dot(up, eye), simd_dot(direction, eye), 1)
        ))
        let f: Float = 1 / tan(.pi / 5.5)
        let near: Float = 0.5
        let far: Float = 4_000
        let perspective = simd_float4x4(columns: (
            SIMD4<Float>(f / aspect, 0, 0, 0),
            SIMD4<Float>(0, f, 0, 0),
            SIMD4<Float>(0, 0, (far + near) / (near - far), -1),
            SIMD4<Float>(0, 0, 2 * far * near / (near - far), 0)
        ))
        return perspective * view
    }
}
