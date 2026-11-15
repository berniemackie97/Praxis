import Foundation
import simd

struct NeuralNebulaCamera {
    var center = SIMD3<Float>.zero
    var desiredCenter = SIMD3<Float>.zero
    var distance: Float = 590
    var desiredDistance: Float = 590
    private var yaw: Float = 0.28
    private var pitch: Float = 0.15

    mutating func orbit(dx: Float, dy: Float) {
        yaw += dx * 0.006
        pitch = max(-1.38, min(1.38, pitch - dy * 0.006))
    }

    mutating func pan(dx: Float, dy: Float) {
        let rate = max(0.1, distance / 600)
        desiredCenter += SIMD3<Float>(-dx * rate, dy * rate, 0)
    }

    mutating func settle(weight: Float) {
        center += (desiredCenter - center) * weight
        distance += (desiredDistance - distance) * weight
    }

    func projection(aspect: Float) -> simd_float4x4 {
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
