import Foundation
import MetalKit
import QuartzCore
import simd

private struct NeuralNebulaUniforms {
    var viewProjection: simd_float4x4
    var time: Float
    var opacity: Float
    var pointScale: Float
    var padding: Float = 0
}

@MainActor
final class NeuralNebulaRenderer: NSObject, MTKViewDelegate {
    var onEnterZone: ((String) -> Void)?
    var onReturnToZones: (() -> Void)?
    var onSelectNode: ((String?) -> Void)?
    var onSelectEdge: ((String) -> Void)?
    /// Projected galaxy label anchors, star label anchors (inside a galaxy) and the entered galaxy.
    var onFrame: (([String: CGPoint], [String: CGPoint], String?) -> Void)?

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
    private var systemPoints = Batch()
    private var systemLines = Batch()
    private var zoneRanges: [String: Range<Int>] = [:]
    private var galaxyShape: Galaxy.Shape?
    private var enteredGalaxy: String?
    private var labelledStars: [String] = []
    private var galaxyOpacity: Float = 0
    private var overview: GraphVisualOverview?
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

    init?(view: MTKView?) {
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
            // Additive light: overlapping stars and gas brighten like real exposure.
            attachment.destinationRGBBlendFactor = .one
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
        guard let view else { return }
        view.device = device
        view.colorPixelFormat = .bgra8Unorm
        view.depthStencilPixelFormat = .depth32Float
        view.clearColor = MTLClearColor(red: 0.008, green: 0.015, blue: 0.039, alpha: 1)
        view.preferredFramesPerSecond = 60
        view.isPaused = false
    }

    func update(overview: GraphVisualOverview?, nodes: [GraphNode], edges: [GraphEdge],
                regionType: String?, focusId: String?,
                selectedNodeId: String?, selectedEdgeId: String?,
                reduceMotion: Bool, highContrast: Bool, active: Bool) {
        self.reduceMotion = reduceMotion
        let levelChanged = self.regionType != regionType || self.focusId != focusId
        let focusWasMissing = focusId != nil && focusId.flatMap { placement.nodeCenters[$0] } == nil
        let topologyChanged = self.overview != overview || graphNodes != nodes ||
            graphEdges != edges || levelChanged
        let contrastChanged = self.highContrast != highContrast
        let nodeSelectionChanged = self.selectedNodeId != selectedNodeId
        let edgeSelectionChanged = self.selectedEdgeId != selectedEdgeId
        self.overview = overview
        graphNodes = nodes
        graphEdges = edges
        self.regionType = regionType
        self.focusId = focusId
        self.selectedNodeId = selectedNodeId
        self.selectedEdgeId = selectedEdgeId
        self.highContrast = highContrast
        if topologyChanged || contrastChanged {
            rebuild(reposition: levelChanged, focusWasMissing: focusWasMissing,
                    updateLayout: topologyChanged)
        } else {
            if nodeSelectionChanged {
                detailPoints = buffer(NeuralNebulaGeometry.detailPoints(
                    nodes: nodes, placement: placement,
                    selectedNodeId: selectedNodeId, highContrast: highContrast))
                // Selecting a star flies into its system; clearing it pulls back out to the galaxy.
                if enteredGalaxy != nil, focusId == nil { frameLevel() }
                if reduceMotion {
                    camera.center = camera.desiredCenter
                    camera.distance = camera.desiredDistance
                }
            }
            if edgeSelectionChanged {
                detailLines = buffer(NeuralNebulaGeometry.detailLines(
                    edges: edges, placement: placement,
                    selectedEdgeId: selectedEdgeId, highContrast: highContrast))
            }
        }
        view?.enableSetNeedsDisplay = reduceMotion
        view?.isPaused = !active
        if active { view?.setNeedsDisplay(view?.bounds ?? .zero) }
    }

    private func buffer(_ vertices: [NeuralNebulaVertex]) -> Batch {
        guard !vertices.isEmpty else { return Batch() }
        let value = vertices.withUnsafeBufferPointer { data in
            device.makeBuffer(bytes: data.baseAddress!,
                              length: data.count * MemoryLayout<NeuralNebulaVertex>.stride,
                              options: .storageModeShared)
        }
        return Batch(buffer: value, count: value == nil ? 0 : vertices.count)
    }

    private func rebuild(reposition: Bool, focusWasMissing: Bool, updateLayout: Bool) {
        guard let overview else {
            placement = NeuralNebulaPlacement(zoneCenters: [:], zoneSizes: [:], nodeCenters: [:])
            zonePoints = Batch()
            zoneLines = Batch()
            detailPoints = Batch()
            detailLines = Batch()
            detailOpacity = 0
            camera.desiredCenter = .zero
            camera.desiredDistance = 560
            return
        }
        let visibleRegion = (regionType ?? focusId).flatMap { id in
            overview.regions.contains(where: { $0.id == id && $0.count > 0 }) ? id : nil
        }
        if updateLayout {
            placement = NeuralNebulaLayout.place(zones: overview, nodes: graphNodes,
                                                  edges: graphEdges, regionId: visibleRegion)
        }
        enteredGalaxy = visibleRegion
        galaxyShape = visibleRegion.flatMap { id in
            placement.zoneCenters[id].map { Galaxy.shape(id: id, center: $0, size: placement.zoneSizes[id] ?? 12) }
        }
        labelledStars = graphNodes.sorted { $0.mentions == $1.mentions ? $0.entityId < $1.entityId : $0.mentions > $1.mentions }
            .prefix(10).map(\.entityId)
        let geometry = NeuralNebulaGeometry.build(overview: overview, placement: placement,
                                                  nodes: graphNodes, edges: graphEdges,
                                                  selectedNodeId: selectedNodeId,
                                                  selectedEdgeId: selectedEdgeId,
                                                  highContrast: highContrast)
        zonePoints = buffer(geometry.zonePoints)
        zoneLines = buffer(geometry.zoneLines)
        detailPoints = buffer(geometry.detailPoints)
        detailLines = buffer(geometry.detailLines)
        zoneRanges = geometry.zoneRanges
        if reposition {
            frameLevel()
        } else if focusWasMissing, let focusId, let point = placement.nodeCenters[focusId] {
            camera.desiredCenter = point
        }
        if reduceMotion {
            camera.center = camera.desiredCenter
            camera.distance = camera.desiredDistance
            detailOpacity = (regionType != nil || focusId != nil) && !graphNodes.isEmpty ? 1 : 0
            galaxyOpacity = enteredGalaxy == nil ? 0 : 1
        }
    }

    /// Distance that frames the entered galaxy's whole disc.
    private var galaxyDistance: Float { max(60, (galaxyShape?.radius ?? 56) * 1.85) }
    private static let starSystemDistance: Float = 22

    /// Frames the current tier: the universe, the entered galaxy, or a selected star's system.
    private func frameLevel() {
        if let selectedNodeId, enteredGalaxy != nil, let star = placement.nodeCenters[selectedNodeId] {
            camera.desiredCenter = star
            camera.desiredDistance = Self.starSystemDistance
        } else if let focusId, let point = placement.nodeCenters[focusId] {
            camera.desiredCenter = point
            camera.desiredDistance = 79
        } else if let shape = galaxyShape {
            camera.desiredCenter = shape.center
            camera.desiredDistance = galaxyDistance
        } else {
            camera.desiredCenter = .zero
            camera.desiredDistance = 560
        }
    }

    /// One tier up: out of a star system to its galaxy, or out of a galaxy to the universe.
    func climb() {
        if selectedNodeId != nil, enteredGalaxy != nil { onSelectNode?(nil) } else { onReturnToZones?() }
    }

    func orbit(dx: Float, dy: Float) {
        camera.orbit(dx: dx, dy: dy)
        invalidate()
    }

    func pan(dx: Float, dy: Float) {
        camera.pan(dx: dx, dy: dy)
        invalidate()
    }

    private func invalidate() {
        if reduceMotion { view?.setNeedsDisplay(view?.bounds ?? .zero) }
    }

    /// Continuous zoom through the tiers. Zooming in drifts toward what is under the pointer, so
    /// the camera flies into the galaxy (then the star) being pointed at; crossing a tier's
    /// threshold enters it, and zooming back out climbs up again.
    func zoom(by factor: Float, at point: CGPoint?) {
        let inside = regionType != nil || focusId != nil
        camera.desiredDistance = max(inside ? 7 : 48, min(1_400, camera.desiredDistance * factor))
        let zoomingIn = factor < 1
        if !inside, let point, let zone = zone(at: point) {
            if let anchor = placement.zoneCenters[zone], zoomingIn {
                camera.desiredCenter += (anchor - camera.desiredCenter) * 0.22
            }
            if camera.desiredDistance < 340 { onEnterZone?(zone) }
        } else if inside && camera.desiredDistance > 450 {
            onReturnToZones?()
        } else if enteredGalaxy != nil, focusId == nil {
            if selectedNodeId != nil, camera.desiredDistance > galaxyDistance * 0.55 {
                // Pulled out of a star system: back to the whole galaxy.
                onSelectNode?(nil)
            } else if selectedNodeId == nil, zoomingIn, let point, let star = nearestNode(to: point, within: 40),
                      let anchor = placement.nodeCenters[star] {
                camera.desiredCenter += (anchor - camera.desiredCenter) * 0.2
                if camera.desiredDistance < galaxyDistance * 0.3 { onSelectNode?(star) }
            }
        }
        invalidate()
    }

    func setToolbarZoom(_ zoom: CGFloat, revision: Int) {
        guard revision != zoomRevision else { return }
        let ratio = Float(zoom / lastToolbarZoom)
        lastToolbarZoom = zoom
        zoomRevision = revision
        if zoom == 1 {
            frameLevel()
            invalidate()
        } else {
            self.zoom(by: ratio, at: nil)
        }
    }

    func click(at point: CGPoint) {
        if regionType != nil || focusId != nil {
            if let node = nearestNode(to: point) { onSelectNode?(node); return }
            if let edge = nearestEdge(to: point) { onSelectEdge?(edge); return }
            if selectedNodeId != nil { onSelectNode?(nil) }
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
            let distance = hypot(position.x - point.x, position.y - point.y)
            return distance < 42 ? (entry.key, distance) : nil
        }.min { $0.1 < $1.1 }?.0
    }

    private func nearestNode(to point: CGPoint, within limit: CGFloat = 14) -> String? {
        placement.nodeCenters.compactMap { entry -> (String, CGFloat)? in
            guard let position = projected(entry.value) else { return nil }
            let distance = hypot(position.x - point.x, position.y - point.y)
            return distance < limit ? (entry.key, distance) : nil
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
        camera.settle(weight: weight)
        let desiredOpacity: Float = (regionType != nil || focusId != nil) && !graphNodes.isEmpty ? 1 : 0
        detailOpacity += (desiredOpacity - detailOpacity) * weight
        galaxyOpacity += ((enteredGalaxy == nil ? 0 : 1) - galaxyOpacity) * weight
        buildStarSystem(time: reduceMotion ? 0 : Float(now))
        guard let pass = view.currentRenderPassDescriptor,
              let drawable = view.currentDrawable,
              let command = queue.makeCommandBuffer(),
              let encoder = command.makeRenderCommandEncoder(descriptor: pass) else { return }
        encoder.setDepthStencilState(depthState)
        let aspect = max(0.1, Float(view.drawableSize.width / max(1, view.drawableSize.height)))
        var uniform = NeuralNebulaUniforms(viewProjection: camera.projection(aspect: aspect),
                                            time: reduceMotion ? 0 : Float(now),
                                            opacity: 1, pointScale: Float(view.drawableSize.height))
        drawScene(uniform: &uniform, encoder: encoder)
        encoder.endEncoding()
        command.present(drawable)
        command.commit()
        if let onFrame {
            let positions = placement.zoneCenters.reduce(into: [String: CGPoint]()) { result, entry in
                if let point = projected(entry.value) { result[entry.key] = point }
            }
            var stars: [String: CGPoint] = [:]
            if enteredGalaxy != nil, detailOpacity > 0.5 {
                var ids = selectedNodeId == nil ? labelledStars : [selectedNodeId!] + planetIds
                if selectedNodeId == nil, camera.distance > galaxyDistance * 1.6 { ids = [] }
                for id in ids {
                    if let point = placement.nodeCenters[id].flatMap(projected),
                       view.bounds.insetBy(dx: -20, dy: -20).contains(point) { stars[id] = point }
                }
            }
            onFrame(positions, stars, enteredGalaxy)
        }
    }

    /// Renders the current scene offscreen after settling the camera, for tests and review.
    func snapshot(size: CGSize) -> CGImage? {
        camera.center = camera.desiredCenter
        camera.distance = camera.desiredDistance
        detailOpacity = (regionType != nil || focusId != nil) && !graphNodes.isEmpty ? 1 : 0
        let width = Int(size.width), height = Int(size.height)
        let color = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .bgra8Unorm, width: width, height: height, mipmapped: false)
        color.usage = [.renderTarget, .shaderRead]
        color.storageMode = .shared
        let depth = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .depth32Float, width: width, height: height, mipmapped: false)
        depth.usage = .renderTarget
        depth.storageMode = .private
        guard let target = device.makeTexture(descriptor: color), let depthTarget = device.makeTexture(descriptor: depth),
              let command = queue.makeCommandBuffer() else { return nil }
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].texture = target
        pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].storeAction = .store
        pass.colorAttachments[0].clearColor = MTLClearColor(red: 0.008, green: 0.015, blue: 0.039, alpha: 1)
        pass.depthAttachment.texture = depthTarget
        pass.depthAttachment.loadAction = .clear
        pass.depthAttachment.storeAction = .dontCare
        guard let encoder = command.makeRenderCommandEncoder(descriptor: pass) else { return nil }
        encoder.setDepthStencilState(depthState)
        var uniform = NeuralNebulaUniforms(viewProjection: camera.projection(aspect: Float(size.width / size.height)),
                                            time: 0, opacity: 1, pointScale: Float(size.height))
        galaxyOpacity = enteredGalaxy == nil ? 0 : 1
        buildStarSystem(time: 1.3)
        drawScene(uniform: &uniform, encoder: encoder)
        encoder.endEncoding()
        command.commit()
        command.waitUntilCompleted()
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        target.getBytes(&bytes, bytesPerRow: width * 4, from: MTLRegionMake2D(0, 0, width, height), mipmapLevel: 0)
        guard let provider = CGDataProvider(data: Data(bytes) as CFData) else { return nil }
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                       space: CGColorSpaceCreateDeviceRGB(),
                       bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
    }

    /// The universe dims as a galaxy is entered, except that galaxy, which stays lit around its
    /// member stars; a selected star's planets draw on top.
    private func drawScene(uniform: inout NeuralNebulaUniforms, encoder: any MTLRenderCommandEncoder) {
        let baseOpacity = 1 - detailOpacity * 0.8
        let litOpacity = baseOpacity + (1 - detailOpacity * 0.45 - baseOpacity) * galaxyOpacity
        draw(zoneLines, lines: true, opacity: baseOpacity * (1 - galaxyOpacity * 0.6), uniform: &uniform, encoder: encoder)
        draw(detailLines, lines: true, opacity: detailOpacity * (selectedNodeId == nil ? 1 : 0.3), uniform: &uniform, encoder: encoder)
        if let galaxy = enteredGalaxy, let range = zoneRanges[galaxy] {
            draw(zonePoints, lines: false, opacity: baseOpacity, range: 0..<range.lowerBound, uniform: &uniform, encoder: encoder)
            draw(zonePoints, lines: false, opacity: litOpacity, range: range, uniform: &uniform, encoder: encoder)
            draw(zonePoints, lines: false, opacity: baseOpacity, range: range.upperBound..<zonePoints.count,
                 uniform: &uniform, encoder: encoder)
        } else {
            draw(zonePoints, lines: false, opacity: baseOpacity, uniform: &uniform, encoder: encoder)
        }
        draw(detailPoints, lines: false, opacity: detailOpacity, uniform: &uniform, encoder: encoder)
        draw(systemLines, lines: true, opacity: detailOpacity, uniform: &uniform, encoder: encoder)
        draw(systemPoints, lines: false, opacity: detailOpacity, uniform: &uniform, encoder: encoder)
    }

    private var planetIds: [String] = []

    /// A selected star's system: each confirmed or proposed relation is a planet on its own orbit
    /// in the galaxy's plane (inner orbits run faster, as Kepler would have it), tethered by a faint
    /// line to the star it relates to. Rebuilt per frame; it is a few hundred vertices at most.
    private func buildStarSystem(time: Float) {
        guard let selectedNodeId, enteredGalaxy != nil, let star = placement.nodeCenters[selectedNodeId],
              let shape = galaxyShape else {
            systemPoints = Batch()
            systemLines = Batch()
            planetIds = []
            return
        }
        let relations = graphEdges.filter { $0.subjectId == selectedNodeId || $0.objectId == selectedNodeId }
            .sorted { $0.relationId < $1.relationId }.prefix(14)
        let u = shape.rotation.act(SIMD3<Float>(1, 0, 0)), v = shape.rotation.act(SIMD3<Float>(0, 1, 0)), normal = shape.normal
        var points: [NeuralNebulaVertex] = []
        var lines: [NeuralNebulaVertex] = []
        // Corona: layered warm glow that breathes slowly.
        let breath = 1 + 0.06 * sin(time * 1.7)
        points.append(NeuralNebulaVertex(position: star, color: SIMD4<Float>(1, 0.62, 0.3, 0.10), size: 14 * breath))
        points.append(NeuralNebulaVertex(position: star, color: SIMD4<Float>(1, 0.85, 0.6, 0.22), size: 5.5 * breath))
        var ids: [String] = []
        for (index, relation) in relations.enumerated() {
            let other = relation.subjectId == selectedNodeId ? relation.objectId : relation.subjectId
            ids.append(other)
            let hash = GraphRegionIdentity.hash(relation.relationId)
            let orbit = 2.4 + Float(index) * 0.95
            let tilt = (Float(hash & 0xFF) / 255 - 0.5) * 0.3
            let phase = Float((hash >> 8) & 0xFFFF) / 65_535 * 2 * .pi
            let angle = phase + time * 1.6 / pow(orbit, 1.5)
            func at(_ theta: Float) -> SIMD3<Float> {
                star + (u * cos(theta) + v * sin(theta)) * orbit + normal * sin(theta) * orbit * tilt
            }
            let color: SIMD3<Float>
            switch relation.evidenceStatus {
            case "suggested": color = [1, 0.71, 0.33]
            case "contradicted": color = [1, 0.41, 0.48]
            default:
                let tone = NeuralNebulaLayout.color(for: relation.predicate)
                color = SIMD3<Float>(tone.x, tone.y, tone.z)
            }
            let selected = relation.relationId == selectedEdgeId
            let segments = 72
            for step in 0..<segments {
                lines.append(NeuralNebulaVertex(position: at(Float(step) / Float(segments) * 2 * .pi),
                                                color: SIMD4<Float>(color, selected ? 0.55 : 0.24), size: 1))
                lines.append(NeuralNebulaVertex(position: at(Float(step + 1) / Float(segments) * 2 * .pi),
                                                color: SIMD4<Float>(color, selected ? 0.55 : 0.24), size: 1))
            }
            let planet = at(angle)
            points.append(NeuralNebulaVertex(position: planet, color: SIMD4<Float>(color, 0.45), size: selected ? 3.2 : 2.3))
            points.append(NeuralNebulaVertex(position: planet, color: SIMD4<Float>(color * 0.4 + 0.6, 1), size: selected ? 1.0 : 0.7))
            if let target = placement.nodeCenters[other] {
                lines.append(NeuralNebulaVertex(position: planet, color: SIMD4<Float>(color, 0.22), size: 1))
                lines.append(NeuralNebulaVertex(position: target, color: SIMD4<Float>(color, 0.04), size: 1))
            }
        }
        planetIds = ids
        systemPoints = buffer(points)
        systemLines = buffer(lines)
    }

    private func draw(_ batch: Batch, lines: Bool, opacity: Float, range: Range<Int>? = nil,
                      uniform: inout NeuralNebulaUniforms, encoder: any MTLRenderCommandEncoder) {
        guard let buffer = batch.buffer, batch.count > 0, opacity > 0.003 else { return }
        let span = range.map { $0.clamped(to: 0..<batch.count) } ?? 0..<batch.count
        guard !span.isEmpty else { return }
        uniform.opacity = opacity
        encoder.setRenderPipelineState(lines ? linesPipeline : pointsPipeline)
        encoder.setVertexBuffer(buffer, offset: 0, index: 0)
        encoder.setVertexBytes(&uniform, length: MemoryLayout<NeuralNebulaUniforms>.stride, index: 1)
        encoder.drawPrimitives(type: lines ? .line : .point, vertexStart: span.lowerBound, vertexCount: span.count)
    }

}
