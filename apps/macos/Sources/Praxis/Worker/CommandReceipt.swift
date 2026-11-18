import Foundation

struct CommandReceipt: Decodable, Equatable {
    struct ReviewedEntity: Decodable, Equatable {
        let entityId: String
        let name: String
        let type: String?
        let fingerprint: String
    }
    let name: String
    let outcome: String
    let candidates: Int
    let reviewed: [ReviewedEntity]
    let reviewItems: [String]
    let appliedChanges: Int
    let summary: String
    let ownerCorrectionId: String?
    var title: String { name == "correction" ? "Brain correction" : "/" + name }
    var outcomeLabel: String {
        switch outcome {
        case "blocked": "Blocked before work"
        case "no_candidates": "No new candidates"
        case "proposals_ready": "Ready for review"
        case "reviewed": "Reviewed · no repair queued"
        case "applied": "Changes saved"
        case "already_current": "Already current"
        case "recorded_unmapped": "Statement saved · graph unchanged"
        default: "Incomplete"
        }
    }
}
