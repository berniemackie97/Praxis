import Foundation

/// Disclosure and tool-permission settings (ADR 0043).
struct DisclosurePolicy: Decodable, Equatable {
    static let modes = ["local_only", "ask_each_time", "standing_grants", "allow_all"]
    static let toolPermissions = ["read_only", "propose_edits", "allow_edits"]
    static let categories = ["financial", "correspondence", "health", "identity", "personal", "documents", "media", "code", "unclassified"]
    static let fallbackSteps = ["local_if_verified", "hold", "index_only", "skip"]
    static let defaultFallbacks = ["local_if_verified", "hold", "index_only"]

    let mode: String
    let rules: [DisclosureRule]
    let fallbacks: [String: [String]]
    let toolPermission: String
    let holdDuration: String

    func fallbacks(for category: String) -> [String] { fallbacks[category] ?? Self.defaultFallbacks }
}

struct DisclosureRule: Decodable, Identifiable, Equatable {
    let ruleId: String
    let category: String
    let providerId: String
    let purpose: String
    let allow: Bool
    let expiresUtc: String?
    var id: String { ruleId }
}

/// The subset of a requested change the owner needs to judge it.
struct PolicyChangeSummary: Decodable, Equatable {
    let mode: String?
    let addRule: DisclosureRule?
    let removeRuleId: String?
    let fallbackCategory: String?
    let fallbackChain: [String]?
    let toolPermission: String?
}

struct PendingPolicyChange: Decodable, Identifiable, Equatable {
    let changeId: String
    let change: PolicyChangeSummary
    let requestedBy: String
    let rationale: String
    let requestedUtc: String
    var id: String { changeId }
}

struct TierAssignment: Decodable, Equatable {
    let tier: String
    let providerId: String
}

struct DisclosureHold: Decodable, Identifiable, Equatable {
    let holdId: String
    let sourceId: String
    let sourceName: String
    let itemId: String
    let revision: Int
    let processorKey: String
    let category: String
    let providerId: String
    let purpose: String
    let status: String
    let deadlineUtc: String
    let createdUtc: String
    var id: String { holdId }
}

struct ModelInvocation: Decodable, Identifiable, Equatable {
    let tier: String
    let providerId: String
    let remote: Bool
    let purpose: String
    let category: String
    let decisionReason: String
    let modelName: String?
    let inputTokens: Int
    let outputTokens: Int
    let outcome: String
    let startedUtc: String
    var id: String { "\(startedUtc)-\(providerId)-\(tier)-\(outcome)" }
}

struct PolicyData: Decodable {
    let policy: DisclosurePolicy
    let tiers: [TierAssignment]
    let pending: [PendingPolicyChange]
}
struct PolicyChangeData: Decodable {
    let applied: Bool
    let pendingChangeId: String?
}
struct DisclosureHoldsData: Decodable { let holds: [DisclosureHold] }
struct ModelInvocationsData: Decodable { let invocations: [ModelInvocation] }
