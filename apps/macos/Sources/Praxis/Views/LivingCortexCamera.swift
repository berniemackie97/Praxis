import Foundation
import simd

/// Orbit camera for Living Cortex. Zoomed out it can orbit and gently sway; entering a territory
/// eases it to face the site, so the brain never teleports between levels.
struct LivingCortexCamera {
    static let wholeBrain: Float = 275
    static let territory: Float = 105
    static let neuron: Float = 38

    var center = SIMD3<Float>(0, -8, 0)
    var desiredCenter = SIMD3<Float>(0, -8, 0)
    var distance: Float = Self.wholeBrain
    var desiredDistance: Float = Self.wholeBrain
    var yaw: Float = 0.35
    var desiredYaw: Float = 0.35
    var pitch: Float = 0.12
    var desiredPitch: Float = 0.12

    mutating func orbit(dx: Float, dy: Float) {
        desiredYaw += dx * 0.006
        desiredPitch = max(-1.2, min(1.2, desiredPitch - dy * 0.006))
    }

    mutating func pan(dx: Float, dy: Float) {
        let rate = max(0.05, distance / 700)
        desiredCenter += SIMD3<Float>(-dx * rate * cos(yaw), dy * rate, dx * rate * sin(yaw))
    }

    /// Faces a point on the cortex: yaw toward its hemisphere side, level pitch.
    mutating func face(_ point: SIMD3<Float>, distance target: Float) {
        desiredCenter = point
        desiredDistance = target
        desiredYaw = point.z >= 0 ? 0 : .pi
        desiredPitch = 0
    }

    mutating func reset() {
        desiredCenter = SIMD3<Float>(0, -8, 0)
        desiredDistance = Self.wholeBrain
        desiredPitch = 0.12
    }

    mutating func settle(weight: Float) {
        center += (desiredCenter - center) * weight
        distance = exp(log(distance) + (log(desiredDistance) - log(distance)) * weight)
        yaw += (desiredYaw - yaw) * weight
        pitch += (desiredPitch - pitch) * weight
    }

    func eye(sway: Float) -> SIMD3<Float> {
        let angle = yaw + sway
        return center + SIMD3<Float>(sin(angle) * cos(pitch), sin(pitch), cos(angle) * cos(pitch)) * distance
    }

    func projection(aspect: Float, sway: Float) -> simd_float4x4 {
        let eye = eye(sway: sway)
        let direction = simd_normalize(center - eye)
        let right = simd_normalize(simd_cross(direction, SIMD3<Float>(0, 1, 0)))
        let up = simd_cross(right, direction)
        let view = simd_float4x4(columns: (
            SIMD4<Float>(right.x, up.x, -direction.x, 0),
            SIMD4<Float>(right.y, up.y, -direction.y, 0),
            SIMD4<Float>(right.z, up.z, -direction.z, 0),
            SIMD4<Float>(-simd_dot(right, eye), -simd_dot(up, eye), simd_dot(direction, eye), 1)
        ))
        let f: Float = 1 / tan(.pi / 6)
        let near: Float = 0.5, far: Float = 4_000
        let perspective = simd_float4x4(columns: (
            SIMD4<Float>(f / aspect, 0, 0, 0),
            SIMD4<Float>(0, f, 0, 0),
            SIMD4<Float>(0, 0, (far + near) / (near - far), -1),
            SIMD4<Float>(0, 0, 2 * far * near / (near - far), 0)
        ))
        return perspective * view
    }
}
