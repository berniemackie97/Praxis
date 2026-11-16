import Foundation

/// Owner-facing wording for policy values; one place so every view says the same thing.
enum PolicyLabels {
    static func mode(_ mode: String) -> String {
        switch mode {
        case "local_only": "Local only — nothing leaves this Mac"
        case "ask_each_time": "Ask each time — every remote use waits for you"
        case "standing_grants": "Standing grants — only categories you grant"
        case "allow_all": "Allow all — any category except explicit denials"
        default: mode
        }
    }

    static func toolPermission(_ permission: String) -> String {
        switch permission {
        case "read_only": "Read only"
        case "propose_edits": "Propose edits for review"
        case "allow_edits": "Allow edits"
        default: permission
        }
    }

    static func fallback(_ step: String) -> String {
        switch step {
        case "local_if_verified": "Local model, if its result is verified"
        case "hold": "Hold for my decision"
        case "index_only": "Keep searchable only"
        case "skip": "Skip"
        default: step
        }
    }

    static func category(_ category: String) -> String { category.prefix(1).uppercased() + category.dropFirst() }

    static func change(_ change: PolicyChangeSummary, rules: [DisclosureRule] = []) -> String {
        if let mode = change.mode { return "Change mode to \(Self.mode(mode))" }
        if let permission = change.toolPermission { return "Change agent permission to \(toolPermission(permission))" }
        if let rule = change.addRule {
            return ruleText(rule)
        }
        if let id = change.removeRuleId {
            return rules.first { $0.ruleId == id }.map { "Remove rule: \(ruleText($0))" } ?? "Remove rule \(id) (no longer listed)"
        }
        if let category = change.fallbackCategory, let chain = change.fallbackChain {
            return "Change fallbacks for \(Self.category(category)): \(chain.map(fallback).joined(separator: " → "))"
        }
        return "Policy change"
    }

    private static func ruleText(_ rule: DisclosureRule) -> String {
        let expiration = rule.expiresUtc.map { " (expires \($0))" } ?? " (no expiration)"
        return "\(rule.allow ? "Allow" : "Deny") \(category(rule.category)) → \(rule.providerId) for \(rule.purpose)\(expiration)"
    }

    static func step(_ processorKey: String) -> String {
        switch processorKey {
        case "financial-records-v1": "read the document"
        case "financial-categories-v1": "categorize transactions"
        default: processorKey
        }
    }
}
