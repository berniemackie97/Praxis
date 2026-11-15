import Foundation
import simd

/// The anatomical shape Living Cortex draws: cerebrum with frontal and temporal lobes, cerebellum and
/// brainstem as implicit sagittal regions, with each hemisphere's half-width giving depth. Units are
/// scene units (the cerebrum spans about 200). The front of the brain faces +x, up is +y.
/// Everything drawn from this shape is ornamental; only territories, pathways and neurons carry data.
enum LivingCortexShape {
    static let scale: Float = 100

    enum Part { case cerebrum, cerebellum, stem }

    static func inCerebrum(_ x: Float, _ y: Float) -> Bool {
        let e = x * x + pow((y - 0.10) / 0.66, 2)
        if e < 1 && y > -0.30 { return true }
        if pow((x - 0.18) / 0.56, 2) + pow((y + 0.28) / 0.24, 2) < 1 { return true }
        return pow((x - 0.62) / 0.42, 2) + pow((y + 0.12) / 0.36, 2) < 1 && y > -0.36
    }

    static func inCerebellum(_ x: Float, _ y: Float) -> Bool {
        pow((x + 0.60) / 0.33, 2) + pow((y + 0.50) / 0.19, 2) < 1
    }

    static func inStem(_ x: Float, _ y: Float) -> Bool {
        let ax: Float = -0.16, ay: Float = -0.36, dx: Float = -0.14, dy: Float = -0.66
        let t = ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)
        guard t >= 0, t <= 1 else { return false }
        let px = ax + dx * t, py = ay + dy * t, r = 0.085 - 0.02 * t
        return (x - px) * (x - px) + (y - py) * (y - py) < r * r
    }

    static func part(_ x: Float, _ y: Float) -> Part? {
        if inCerebrum(x, y) { return .cerebrum }
        if inCerebellum(x, y) { return .cerebellum }
        if inStem(x, y) { return .stem }
        return nil
    }

    static func halfWidth(_ x: Float, _ y: Float, _ part: Part) -> Float {
        switch part {
        case .cerebrum: 0.70 * sqrt(max(0.03, 1 - pow(x / 1.08, 2) - pow((y - 0.02) / 0.86, 2)))
        case .cerebellum: 0.44 * sqrt(max(0.03, 1 - pow((x + 0.6) / 0.36, 2) - pow((y + 0.5) / 0.22, 2)))
        case .stem: 0.075
        }
    }

    /// A point on the right hemisphere's outer surface (unit coordinates in, scene units out).
    static func surface(_ x: Float, _ y: Float) -> SIMD3<Float> {
        let part = part(x, y) ?? .cerebrum
        return SIMD3<Float>(x, y, halfWidth(x, y, part) * 0.97) * scale
    }

    static func field(_ x: Float, _ y: Float) -> Float {
        1.9 * sin(2.7 * x + 1.1 * y + 0.3) + 1.4 * cos(3.3 * y - 2.1 * x)
            + 0.9 * sin(5.1 * x * y + 1.7) + 0.6 * sin(7.3 * y + 0.4 * x)
    }
}

/// A small deterministic generator, so the brain looks the same on every launch.
struct CortexRandom {
    private var state: UInt64
    init(seed: UInt64) { state = seed &+ 0x9E37_79B9_7F4A_7C15 }

    mutating func next() -> Float {
        state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
        return Float((state >> 40) & 0xFFFFFF) / Float(0x1000000)
    }
}
