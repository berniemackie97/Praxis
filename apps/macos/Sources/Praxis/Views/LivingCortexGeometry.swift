import Foundation
import simd

/// Vertex layout shared with LivingCortex.metal (48-byte stride).
struct CortexVertex {
    var position: SIMD3<Float>
    var color: SIMD4<Float>
    var size: Float
    var along: Float = 0
    var phase: Float = -1
}

/// A territory pinned to the cortex: one knowledge region at an anatomical site. The site is a
/// metaphor for where the region sits in the owner's knowledge, never a claim about biology.
struct CortexTerritory: Equatable {
    let id: String
    let label: String
    let entities: Int
    let forming: Bool
    let unit: SIMD2<Float>
    let position: SIMD3<Float>
    let radius: Float
    let color: SIMD3<Float>
}

/// The brain's ornamental body: shell points, folds, outline and sulci, generated once.
struct CortexShell {
    struct Dot {
        let unit: SIMD2<Float>
        let position: SIMD3<Float>
        let big: Bool
        let stem: Bool
        let phase: Float
    }

    let dots: [Dot]
    let folds: [[SIMD3<Float>]]
    let outlines: [[SIMD3<Float>]]

    static func build(detail: Int = 2600, folds foldCount: Int = 520) -> CortexShell {
        var random = CortexRandom(seed: 20_261_115)
        let s = LivingCortexShape.scale
        var dots: [Dot] = []
        dots.reserveCapacity(detail)
        while dots.count < detail {
            let x = random.next() * 2.3 - 1.15, y = random.next() * 2.2 - 1.1
            guard let part = LivingCortexShape.part(x, y) else { continue }
            if part == .stem && random.next() < 0.6 { continue }
            let width = LivingCortexShape.halfWidth(x, y, part)
            let side: Float = random.next() < 0.5 ? -1 : 1
            let shell = random.next() < 0.84
            let z = shell ? side * width * (0.9 + 0.1 * random.next()) : (random.next() * 2 - 1) * width * 0.8
            dots.append(Dot(unit: [x, y], position: SIMD3<Float>(x, y, z) * s, big: random.next() < 0.06,
                            stem: part == .stem, phase: random.next()))
        }

        var folds: [[SIMD3<Float>]] = []
        for index in 0..<foldCount {
            var x: Float = 0, y: Float = 0, tries = 0
            repeat {
                x = random.next() * 2.1 - 1.05
                y = random.next() * 1.5 - 0.55
                tries += 1
            } while !LivingCortexShape.inCerebrum(x, y) && tries < 50
            let side: Float = index % 2 == 0 ? 1 : -1
            let phase = random.next() * 6.283
            // Each fold reads its own offset of the field and stays short, so folds scatter into
            // gyri instead of converging on the field's attractor curves.
            let offset = SIMD2<Float>(random.next() * 4, random.next() * 4)
            let steps = 14 + Int(random.next() * 20)
            var line: [SIMD3<Float>] = []
            for step in 0..<steps {
                let angle = LivingCortexShape.field(x + offset.x, y + offset.y) + 1.1 * sin(Float(step) * 0.7 + phase)
                x += cos(angle) * 0.015
                y += sin(angle) * 0.015
                guard LivingCortexShape.inCerebrum(x, y) else { break }
                line.append(SIMD3<Float>(x, y, side * LivingCortexShape.halfWidth(x, y, .cerebrum) * 0.985) * s)
            }
            if line.count >= 10 { folds.append(line) }
        }
        for row in 0..<26 {
            let base = -0.36 - Float(row) * 0.0105
            let side: Float = row % 2 == 0 ? 1 : -1
            var line: [SIMD3<Float>] = []
            var x: Float = -0.93
            while x <= -0.27 {
                let y = base + 0.025 * sin(x * 40 + Float(row))
                if LivingCortexShape.inCerebellum(x, y) {
                    line.append(SIMD3<Float>(x, y, side * LivingCortexShape.halfWidth(x, y, .cerebellum) * 0.98) * s)
                }
                x += 0.012
            }
            if line.count >= 6 { folds.append(line) }
        }
        // Central and lateral sulci on both hemispheres, and the longitudinal fissure along the top.
        for side: Float in [1, -1] {
            for points in [[(0.40, -0.10), (0.25, -0.04), (0.08, 0.0), (-0.10, 0.06), (-0.30, 0.12)],
                           [(0.02, 0.74), (0.07, 0.55), (0.13, 0.36), (0.18, 0.16), (0.24, 0.0)]] as [[(Float, Float)]] {
                folds.append(points.map { SIMD3<Float>($0.0, $0.1, side * LivingCortexShape.halfWidth($0.0, $0.1, .cerebrum) * 0.99) * s })
            }
        }

        func outline(center: SIMD2<Float>, test: (Float, Float) -> Bool) -> [SIMD3<Float>] {
            stride(from: Float(0), through: 6.2832, by: 0.03).map { angle in
                var radius: Float = 0.05
                while radius < 1.4 && test(center.x + cos(angle) * radius, center.y + sin(angle) * radius) { radius += 0.006 }
                return SIMD3<Float>(center.x + cos(angle) * radius, center.y + sin(angle) * radius, 0) * s
            }
        }
        let outlines = [outline(center: [0.05, 0.05], test: LivingCortexShape.inCerebrum),
                        outline(center: [-0.6, -0.5], test: LivingCortexShape.inCerebellum)]
        return CortexShell(dots: dots, folds: folds, outlines: outlines)
    }
}

enum LivingCortexLayout {
    /// Anatomical sites, largest lobe first: parietal, frontal, occipital, temporal, motor strip,
    /// premotor, temporo-occipital, prefrontal, insula, superior temporal, temporal pole, cingulate.
    /// The cerebellum is kept for a root's loose files (small, practical notes).
    static let sites: [SIMD2<Float>] = [
        [-0.30, 0.50], [0.66, 0.20], [-0.78, 0.10], [0.14, -0.30], [0.16, 0.52], [0.46, 0.44],
        [-0.56, -0.16], [0.88, -0.06], [0.40, -0.04], [-0.16, -0.08], [0.62, -0.26], [-0.05, 0.28],
        [-0.55, 0.30], [0.35, 0.18], [-0.30, 0.10], [0.80, 0.30]
    ]
    static let cerebellum: SIMD2<Float> = [-0.60, -0.50]

    static func territories(_ regions: [GraphKnowledgeRegion], map: GraphRegionMap?) -> [CortexTerritory] {
        let largest = Float(max(1, regions.map(\.entities).max() ?? 1))
        var siteIndex = 0
        var usedCerebellum = false
        return regions.sorted { $0.entities == $1.entities ? $0.regionId < $1.regionId : $0.entities > $1.entities }
            .map { region in
                var unit: SIMD2<Float>
                var mirrored = false
                if region.kind == "loose" && !usedCerebellum {
                    unit = cerebellum
                    usedCerebellum = true
                } else {
                    unit = sites[siteIndex % sites.count]
                    mirrored = siteIndex >= sites.count
                    siteIndex += 1
                }
                var position = LivingCortexShape.surface(unit.x, unit.y)
                if mirrored { position.z = -position.z }
                let radius: Float = region.forming ? 0.14 : 0.10 + 0.22 * sqrt(Float(region.entities) / largest)
                return CortexTerritory(id: region.regionId, label: region.label, entities: region.entities,
                                       forming: region.forming, unit: unit, position: position, radius: radius,
                                       color: BrainPalette.rgb(for: region, in: map))
            }
    }

    static func shellPoints(_ shell: CortexShell, territories: [CortexTerritory], highContrast: Bool) -> [CortexVertex] {
        let base: SIMD3<Float> = [1.0, 0.30, 0.20]
        return shell.dots.map { dot in
            var color = base
            var alpha: Float = dot.stem ? 0.30 : 0.50
            if let territory = nearest(territories, unit: dot.unit, side: dot.position.z) {
                color = territory.forming ? BrainPalette.forming : territory.color
                alpha = territory.forming ? 0.35 : 0.75
            }
            if highContrast { alpha = min(1, alpha + 0.2) }
            return CortexVertex(position: dot.position, color: SIMD4<Float>(color, alpha),
                                size: dot.big ? 5.5 : 2.2, phase: dot.phase)
        }
    }

    static func shellLines(_ shell: CortexShell, territories: [CortexTerritory], highContrast: Bool) -> [CortexVertex] {
        var vertices: [CortexVertex] = []
        for fold in shell.folds {
            let middle = fold[fold.count / 2]
            let unit = SIMD2<Float>(middle.x, middle.y) / LivingCortexShape.scale
            let territory = nearest(territories, unit: unit, side: middle.z)
            let color = territory.map { $0.forming ? BrainPalette.forming : $0.color } ?? [1.0, 0.36, 0.26]
            let alpha: Float = (territory == nil ? 0.20 : 0.34) + (highContrast ? 0.15 : 0)
            for index in 1..<fold.count {
                vertices.append(CortexVertex(position: fold[index - 1], color: SIMD4<Float>(color, alpha), size: 1))
                vertices.append(CortexVertex(position: fold[index], color: SIMD4<Float>(color, alpha), size: 1))
            }
        }
        for loop in shell.outlines {
            for index in 0..<loop.count {
                let next = loop[(index + 1) % loop.count]
                vertices.append(CortexVertex(position: loop[index], color: [1.0, 0.55, 0.42, 0.28], size: 1))
                vertices.append(CortexVertex(position: next, color: [1.0, 0.55, 0.42, 0.28], size: 1))
            }
        }
        return vertices
    }

    static func territoryGlows(_ territories: [CortexTerritory], openId: String?) -> [CortexVertex] {
        territories.flatMap { territory -> [CortexVertex] in
            let open = territory.id == openId
            let alpha: Float = territory.forming ? 0.2 : open ? 0.7 : 0.5
            let color = territory.forming ? BrainPalette.forming : territory.color
            let size = territory.radius * LivingCortexShape.scale * 2.9
            return [CortexVertex(position: territory.position, color: SIMD4<Float>(color, alpha), size: size,
                                 phase: Float(GraphRegionIdentity.hash(territory.id) % 997) / 997),
                    CortexVertex(position: territory.position, color: SIMD4<Float>(1, 0.95, 0.9, territory.forming ? 0.15 : 0.6),
                                 size: 9)]
        }
    }

    /// Evidence links drawn as arcs through the brain's interior, with a travelling pulse.
    static func pathways(_ links: [GraphVisualLink], territories: [CortexTerritory], highContrast: Bool) -> [CortexVertex] {
        let byId = Dictionary(uniqueKeysWithValues: territories.map { ($0.id, $0) })
        let strongest = Float(max(1, links.map(\.count).max() ?? 1))
        var vertices: [CortexVertex] = []
        for link in links {
            guard let a = byId[link.subjectId], let b = byId[link.objectId] else { continue }
            let weight = sqrt(Float(link.count) / strongest)
            let alpha = 0.09 + 0.32 * weight + (highContrast ? 0.2 : 0)
            let phase = Float(GraphRegionIdentity.hash(link.subjectId + link.objectId) % 1009) / 1009
            // Arc through the interior; each pair bends its own way so parallel pathways never stack.
            let bend = SIMD3<Float>(cos(phase * 6.283), sin(phase * 6.283), 0) * 14
            let control = (a.position + b.position) * 0.5 * SIMD3<Float>(0.6, 0.6, 0.1) + bend
            let segments = 22
            for step in 0..<segments {
                let t0 = Float(step) / Float(segments), t1 = Float(step + 1) / Float(segments)
                for t in [t0, t1] {
                    let u = 1 - t
                    let point = u * u * a.position + 2 * u * t * control + t * t * b.position
                    let color = a.color * u + b.color * t
                    vertices.append(CortexVertex(position: point, color: SIMD4<Float>(color, alpha), size: 1, along: t, phase: phase))
                }
            }
        }
        return vertices
    }

    /// Neuron positions for an open territory's members: a disc facing the viewer at the site.
    static func neuronPositions(_ nodes: [GraphNode], territory: CortexTerritory) -> [String: SIMD3<Float>] {
        let radius = max(18, territory.radius * LivingCortexShape.scale * 0.95)
        let facing: Float = territory.position.z >= 0 ? 1 : -1
        var positions: [String: SIMD3<Float>] = [:]
        for (index, node) in nodes.enumerated() {
            let angle = Float(index) * 2.399_96
            let distance = radius * sqrt((Float(index) + 0.5) / Float(max(1, nodes.count)))
            let lift = Float(GraphRegionIdentity.hash(node.entityId) % 100) / 100 * 3
            positions[node.entityId] = territory.position + SIMD3<Float>(cos(angle) * distance, sin(angle) * distance, facing * (6 + lift))
        }
        return positions
    }

    static func neurons(_ nodes: [GraphNode], positions: [String: SIMD3<Float>], color: SIMD3<Float>,
                        selectedId: String?) -> [CortexVertex] {
        nodes.compactMap { node in
            guard let position = positions[node.entityId] else { return nil }
            let selected = node.entityId == selectedId
            let size = (selected ? 9 : 4.5) + log2(Float(max(1, node.mentions)) + 1) * 1.6
            let tint: SIMD3<Float> = selected ? [1, 1, 1] : color
            return CortexVertex(position: position, color: SIMD4<Float>(tint, node.status == "suggested" ? 0.5 : 0.95),
                                size: size, phase: Float(GraphRegionIdentity.hash(node.entityId) % 101) / 101)
        }
    }

    /// Synapses: real relations between neurons, pulsing from subject to object.
    static func synapses(_ edges: [GraphEdge], positions: [String: SIMD3<Float>], color: SIMD3<Float>,
                         selectedNodeId: String?, selectedEdgeId: String?) -> [CortexVertex] {
        edges.flatMap { edge -> [CortexVertex] in
            guard let a = positions[edge.subjectId], let b = positions[edge.objectId] else { return [] }
            let touched = selectedNodeId == nil || edge.subjectId == selectedNodeId || edge.objectId == selectedNodeId
            let selected = edge.relationId == selectedEdgeId
            let alpha: Float = selected ? 1 : touched ? (edge.evidenceStatus == "suggested" ? 0.25 : 0.6) : 0.08
            let tint: SIMD3<Float> = selected ? [1, 1, 1] : color
            let phase = Float(GraphRegionIdentity.hash(edge.relationId) % 997) / 997
            return [CortexVertex(position: a, color: SIMD4<Float>(tint, alpha), size: 1, along: 0, phase: phase),
                    CortexVertex(position: b, color: SIMD4<Float>(tint, alpha), size: 1, along: 1, phase: phase)]
        }
    }

    /// Ornamental dendrites around each neuron (never data).
    static func dendrites(_ nodes: [GraphNode], positions: [String: SIMD3<Float>], color: SIMD3<Float>) -> [CortexVertex] {
        var vertices: [CortexVertex] = []
        for node in nodes {
            guard let center = positions[node.entityId] else { continue }
            var random = CortexRandom(seed: GraphRegionIdentity.hash(node.entityId))
            let branches = 5 + Int(random.next() * 4)
            for branch in 0..<branches {
                var angle = Float(branch) / Float(branches) * 6.283 + random.next() * 0.6
                let length = 2.5 + random.next() * 3.5
                var point = center
                for _ in 0..<5 {
                    angle += (random.next() - 0.5) * 0.7
                    let next = point + SIMD3<Float>(cos(angle), sin(angle), 0) * (length / 5)
                    vertices.append(CortexVertex(position: point, color: SIMD4<Float>(color, 0.32), size: 1))
                    vertices.append(CortexVertex(position: next, color: SIMD4<Float>(color, 0.32), size: 1))
                    point = next
                }
            }
        }
        return vertices
    }

    private static func nearest(_ territories: [CortexTerritory], unit: SIMD2<Float>, side: Float) -> CortexTerritory? {
        var best: CortexTerritory?
        var bestDistance = Float.greatestFiniteMagnitude
        for territory in territories where territory.position.z >= 0 || side < 0 {
            let distance = simd_length(territory.unit - unit)
            if distance < territory.radius * 1.35 && distance < bestDistance {
                best = territory
                bestDistance = distance
            }
        }
        return best
    }
}
