import Foundation

/// Durable extraction work for current revisions of active sources.
struct ProcessingBacklog: Decodable {
    let queued: Int
    let running: Int
    let failed: Int
    /// Optional local enrichment still to run; it never blocks intake.
    let enrichmentPending: Int?
}
