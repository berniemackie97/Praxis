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

/// How Praxis should use this Mac's local models.
struct MachinePlan: Decodable, Equatable {
    let profile: String
    let memoryBytes: Int64
    let localModel: String?
    let embeddingModel: String?
    let draftMode: String
    let draftBudgetSeconds: Int
    let enrichment: Bool
    let notes: [String]
}

struct MachineData: Decodable, Equatable {
    let recommended: MachinePlan
    let draftMode: String
    let draftBudgetSeconds: Int
    let secondsPerThousandCharacters: Double?
    let installed: [InstalledModel]
}
