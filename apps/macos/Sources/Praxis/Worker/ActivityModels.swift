import Foundation

/// What the worker is doing now, as the `activity` action reports it.
struct ActivityData: Decodable {
    let calling: [ModelCallInProgress]
    let running: [RunningJob]
    let stages: [StageCount]
    let providers: [ProviderActivity]
    let recentCalls: [RecentModelCall]
    let outcomes: [IntakeOutcomeCount]
    let notices: [ActivityNotice]
    let localSecondsPerThousandCharacters: Double?
}

struct ModelCallInProgress: Decodable, Identifiable {
    let tier: String
    let providerId: String
    let model: String?
    let purpose: String
    let fileName: String?
    let startedUtc: String
    var id: String { "\(startedUtc)-\(providerId)-\(tier)-\(fileName ?? "")" }
}

struct RunningJob: Decodable, Identifiable {
    let itemId: String
    let fileName: String
    let stage: String
    let attempt: Int
    let startedUtc: String
    let leaseUntilUtc: String?
    var id: String { "\(itemId)-\(stage)" }
}

struct StageCount: Decodable, Identifiable {
    let stage: String
    let queued: Int
    let running: Int
    let failed: Int
    let doneToday: Int
    var id: String { stage }
}

struct ProviderActivity: Decodable, Identifiable {
    let providerId: String
    let enabled: Bool
    let dailyLimit: Int
    let callsToday: Int
    let failuresLastHour: Int
    let lastCallUtc: String?
    let lastOutcome: String?
    let roles: [String]
    var id: String { providerId }
}

struct RecentModelCall: Decodable, Identifiable {
    let startedUtc: String
    let tier: String
    let providerId: String
    let model: String?
    let purpose: String
    let outcome: String
    let fileName: String?
    let durationMs: Int?
    let inputTokens: Int
    let outputTokens: Int
    var id: String { "\(startedUtc)-\(providerId)-\(tier)-\(outcome)-\(fileName ?? "")" }
}

struct IntakeOutcomeCount: Decodable, Identifiable {
    let status: String
    let reason: String?
    let count: Int
    var id: String { "\(status)-\(reason ?? "")" }
}

struct ActivityNotice: Decodable, Identifiable {
    let tone: String
    let text: String
    var id: String { text }
}

/// Plain names for the worker's stage, role and outcome codes.
enum ActivityText {
    static func stage(_ key: String) -> String {
        switch key {
        case "source-text-v1": "Read text"
        case "text-segments-v1": "Split into passages"
        case "markdown-heading-v1": "Find headings"
        case "intake-triage-v1": "Triage"
        case "intake-understand-v1": "Understand"
        case "semantic-extract-v2": "Local enrichment"
        case let other where other.hasPrefix("link-"): "Fetch links"
        default: key
        }
    }

    static func role(_ tier: String) -> String {
        switch tier {
        case "local": "Triage"
        case "strong": "Reading"
        case "strongest": "Checking"
        default: tier.capitalized
        }
    }

    static func provider(_ id: String) -> String {
        switch id {
        case "claude-code": "Claude Code"
        case "codex": "Codex"
        case "ollama": "Ollama"
        default: id
        }
    }

    static func outcome(_ code: String) -> String {
        switch code {
        case "completed": "Done"
        case "model_invalid_response": "Unusable answer"
        case "model_timeout": "Timed out"
        case "model_quota_exhausted": "Plan limit reached"
        case "model_unauthenticated": "Needs sign-in"
        case "model_unavailable": "Not reachable"
        default: code.replacingOccurrences(of: "_", with: " ").capitalized
        }
    }

    static func intake(_ outcome: IntakeOutcomeCount) -> String {
        switch (outcome.status, outcome.reason) {
        case ("understood", _): "Understood"
        case ("partially_verified", _): "Partly verified"
        case ("held", _): "Held for your approval"
        case ("index_only", "tier_unavailable"?), ("index_only", "model_quota_exhausted"?): "Waiting for a model"
        case ("index_only", "project_level"?): "Covered by its project"
        case ("index_only", "model_invalid_response"?): "Model answer was unusable"
        case ("index_only", "model_timeout"?): "Model ran out of time"
        case ("index_only", _): "Searchable only"
        default: outcome.status.replacingOccurrences(of: "_", with: " ").capitalized
        }
    }

    static func tone(_ code: String) -> Tone {
        switch code {
        case "critical": .critical
        case "warning": .warning
        default: .accent
        }
    }

    static func elapsed(since iso: String, now: Date) -> String {
        guard let start = DateText.date(iso) else { return "" }
        let seconds = max(0, Int(now.timeIntervalSince(start)))
        return seconds < 60 ? "\(seconds)s" : seconds < 3_600 ? "\(seconds / 60)m \(seconds % 60)s" : "\(seconds / 3_600)h \(seconds % 3_600 / 60)m"
    }

    static func duration(_ milliseconds: Int?) -> String {
        guard let milliseconds else { return "—" }
        return milliseconds < 1_000 ? "\(milliseconds) ms" : String(format: "%.1f s", Double(milliseconds) / 1_000)
    }
}
