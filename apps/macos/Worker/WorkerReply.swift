import Foundation

/// A worker response whose `data` has a feature-specific shape.
struct WorkerReply<Payload: Decodable>: Decodable {
    let ok: Bool
    let data: Payload?
    let error: String?
}
