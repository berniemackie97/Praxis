import Foundation
import simd

/// Simulated galaxies and sky for Knowledge Cosmos. A galaxy's size and star count come from how
/// much the region knows; everything about its look (inclination, arms, bulge, star-forming knots,
/// haze, colour temperature) is ornamental and deterministic per region.
enum Galaxy {
    /// A galaxy's attitude in space and arm pattern. Shared by the procedural stars and by the
    /// region's member entities, so entering a galaxy flies into the same disc it showed from afar.
    struct Shape {
        let center: SIMD3<Float>
        let rotation: simd_quatf
        let radius: Float
        let arms: Int
        let pitch: Float

        func place(_ x: Float, _ y: Float, _ z: Float) -> SIMD3<Float> { center + rotation.act(SIMD3<Float>(x, y, z)) }

        /// Angle of a spiral arm at disc radius `r`.
        func armAngle(_ arm: Int, at r: Float) -> Float {
            Float(arm) * 2 * .pi / Float(arms) + log(max(r, radius * 0.12) / (radius * 0.12)) / tan(pitch)
        }

        /// The disc's normal, the axis everything in it rotates around.
        var normal: SIMD3<Float> { rotation.act(SIMD3<Float>(0, 0, 1)) }
    }

    static func shape(id: String, center: SIMD3<Float>, size: Float) -> Shape {
        var random = CortexRandom(seed: GraphRegionIdentity.hash(id) ^ 0x51A7_C0DE)
        // A real inclination (20-75 degrees) and position angle give each disc its own 3D attitude.
        let inclination = (20 + random.next() * 55) * Float.pi / 180
        let positionAngle = random.next() * 2 * Float.pi
        let rotation = simd_quatf(angle: positionAngle, axis: [0, 0, 1]) * simd_quatf(angle: inclination, axis: [1, 0, 0])
        return Shape(center: center, rotation: rotation, radius: size * 2.4,
                     arms: 2 + Int(random.next() * 3), pitch: 0.22 + random.next() * 0.16)
    }

    static func stars(id: String, center: SIMD3<Float>, size: Float, members: Int, tint: SIMD3<Float>,
                      forming: Bool, highContrast: Bool) -> [NeuralNebulaVertex] {
        var random = CortexRandom(seed: GraphRegionIdentity.hash(id))
        let form = shape(id: id, center: center, size: size)
        let radius = form.radius
        func place(_ x: Float, _ y: Float, _ z: Float) -> SIMD3<Float> { form.place(x, y, z) }
        func gaussian() -> Float { (random.next() + random.next() + random.next() - 1.5) / 1.5 }
        var vertices: [NeuralNebulaVertex] = []

        if forming {
            // An uncondensed nebula: glowing gas, no stars yet.
            for _ in 0..<40 {
                vertices.append(NeuralNebulaVertex(position: place(gaussian() * radius, gaussian() * radius, gaussian() * radius * 0.3),
                                                   color: SIMD4<Float>(tint, highContrast ? 0.12 : 0.05), size: radius * (0.5 + random.next())))
            }
            return vertices
        }

        let warm: SIMD3<Float> = [1.0, 0.82, 0.58], young: SIMD3<Float> = [0.72, 0.82, 1.0], pink: SIMD3<Float> = [1.0, 0.42, 0.68]
        // Core glow and bulge: dense, warm, slightly flattened.
        vertices.append(NeuralNebulaVertex(position: center, color: SIMD4<Float>(warm, 0.22), size: radius * 1.5))
        vertices.append(NeuralNebulaVertex(position: center, color: SIMD4<Float>(1, 0.96, 0.9, 0.85), size: radius * 0.32))
        let bulge = min(420, 80 + members / 3)
        for _ in 0..<bulge {
            let p = place(gaussian() * radius * 0.22, gaussian() * radius * 0.22, gaussian() * radius * 0.13)
            vertices.append(NeuralNebulaVertex(position: p, color: SIMD4<Float>(warm * (0.8 + 0.2 * random.next()), highContrast ? 0.9 : 0.6),
                                               size: 1.1 + random.next() * 1.4))
        }
        // Disc: logarithmic spiral arms, young blue-white stars mixed with the region's colour.
        let arms = form.arms
        let disc = min(1_600, 260 + Int(log2(Double(members) + 1) * 120))
        for number in 0..<disc {
            let arm = number % arms
            let r = radius * (0.12 + 0.88 * sqrt(random.next()))
            // A third of the disc is the old diffuse population; the rest traces the arms loosely.
            let diffuse = random.next() < 0.33
            let theta = diffuse ? random.next() * 2 * .pi
                : form.armAngle(arm, at: r) + gaussian() * 0.75 * (1.15 - r / radius)
            let p = place(cos(theta) * r, sin(theta) * r, gaussian() * radius * 0.035)
            let mix = random.next()
            let color = young * mix + tint * (1 - mix)
            let bright = random.next() < 0.06
            vertices.append(NeuralNebulaVertex(position: p, color: SIMD4<Float>(bright ? [1, 1, 1] : color, highContrast ? 0.85 : (bright ? 0.9 : 0.5)),
                                               size: bright ? 2.4 : 0.9 + random.next() * 1.1))
        }
        // Star-forming knots along the arms and a faint haze across the disc.
        for _ in 0..<(10 + Int(random.next() * 22)) {
            let arm = Int(random.next() * Float(arms))
            let r = radius * (0.35 + 0.6 * random.next())
            let theta = form.armAngle(arm, at: r)
            vertices.append(NeuralNebulaVertex(position: place(cos(theta) * r, sin(theta) * r, 0),
                                               color: SIMD4<Float>(pink, 0.55), size: 3 + random.next() * 3))
        }
        // Dark-edged dust lanes trail just inside each arm (drawn as dim reddish haze: additive light
        // cannot darken, so the lanes read by their warm absorption tint against the blue arms).
        for _ in 0..<(arms * 26) {
            let arm = Int(random.next() * Float(arms))
            let r = radius * (0.2 + 0.75 * random.next())
            let theta = form.armAngle(arm, at: r) - 0.22
            vertices.append(NeuralNebulaVertex(position: place(cos(theta) * r, sin(theta) * r, 0),
                                               color: SIMD4<Float>(0.55, 0.28, 0.16, 0.05), size: radius * 0.16))
        }
        for _ in 0..<28 {
            let r = radius * sqrt(random.next()) * 0.9
            let theta = random.next() * 2 * .pi
            vertices.append(NeuralNebulaVertex(position: place(cos(theta) * r, sin(theta) * r, 0),
                                               color: SIMD4<Float>(tint, highContrast ? 0.1 : 0.06), size: radius * 0.8))
        }
        return vertices
    }

    /// Ornamental sky: stars of varied colour temperature, a faint galactic band and nebula clouds.
    static func sky(highContrast: Bool) -> [NeuralNebulaVertex] {
        var random = CortexRandom(seed: 99)
        var vertices: [NeuralNebulaVertex] = []
        let temperatures: [SIMD3<Float>] = [[0.75, 0.82, 1.0], [1.0, 0.97, 0.92], [1.0, 0.86, 0.65], [1.0, 0.72, 0.6]]
        for _ in 0..<1_500 {
            let direction = simd_normalize(SIMD3<Float>(random.next() * 2 - 1, random.next() * 2 - 1, random.next() * 2 - 1))
            let tone = temperatures[Int(random.next() * Float(temperatures.count)) % temperatures.count]
            vertices.append(NeuralNebulaVertex(position: direction * (700 + random.next() * 500),
                                               color: SIMD4<Float>(tone, (highContrast ? 0.35 : 0.15) + random.next() * 0.35),
                                               size: 0.8 + random.next() * 1.8))
        }
        let band = simd_quatf(angle: 0.5, axis: simd_normalize(SIMD3<Float>(1, 0.3, 0)))
        for _ in 0..<1_200 {
            let angle = random.next() * 2 * .pi
            let spread = (random.next() + random.next() - 1) * 0.12
            let direction = band.act(simd_normalize(SIMD3<Float>(cos(angle), spread, sin(angle))))
            vertices.append(NeuralNebulaVertex(position: direction * 1_000, color: SIMD4<Float>(0.8, 0.82, 1.0, 0.10 + random.next() * 0.12),
                                               size: 0.8 + random.next()))
        }
        for cloud in 0..<9 {
            let tint: SIMD3<Float> = cloud % 3 == 0 ? [0.42, 0.20, 0.75] : cloud % 3 == 1 ? [0.10, 0.32, 0.55] : [0.45, 0.12, 0.45]
            let direction = simd_normalize(SIMD3<Float>(random.next() * 2 - 1, random.next() - 0.5, -0.6 - random.next()))
            vertices.append(NeuralNebulaVertex(position: direction * 900, color: SIMD4<Float>(tint, 0.07), size: 620 + random.next() * 460))
        }
        return vertices
    }
}
