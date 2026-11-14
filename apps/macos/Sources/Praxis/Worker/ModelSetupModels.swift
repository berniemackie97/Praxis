import Foundation

/// A model a provider offers, with the reasoning efforts it accepts.
struct ModelChoice: Decodable, Identifiable, Equatable, Hashable {
    let id: String
    let label: String
    let efforts: [String]
    let defaultEffort: String?
}

/// A model tool found on this Mac (Claude Code, Codex or Ollama) and what it can do.
struct ModelTool: Decodable, Identifiable, Equatable {
    static let claudeCode = "claude-code"
    static let codex = "codex"
    static let ollama = "ollama"

    let id: String
    let path: String?
    let version: String?
    let signedIn: Bool
    let account: String?
    let problem: String?
    let models: [ModelChoice]

    var installed: Bool { path != nil && problem != "not_found" }
}

struct ModelSetupData: Decodable {
    let tools: [ModelTool]
    let providers: [VerificationProvider]
    let allowances: [ProviderAllowance]
    let backends: [AgentBackendProfile]
    let tiers: [TierAssignment]
}

struct ModelToolData: Decodable { let tool: ModelTool }
