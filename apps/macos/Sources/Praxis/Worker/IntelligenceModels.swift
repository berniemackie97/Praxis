import Foundation

/// The owner's local model for enrichment.
struct ModelProfile: Decodable, Equatable {
    let role: String
    let provider: String
    let endpoint: String
    let model: String
    let contextTokens: Int
    let enabled: Bool
}

struct InstalledModel: Decodable, Identifiable {
    let name: String
    let digest: String
    let sizeBytes: Int64
    var id: String { name }
}

/// A local model or explicitly allowed subscription CLI for reviewed verification.
struct VerificationProvider: Decodable, Identifiable, Equatable {
    static let ollama = "ollama"
    static let codex = "codex"
    static let claudeCode = "claude-code"

    let providerId: String
    let executable: String?
    let endpoint: String?
    let model: String?
    let revision: Int
    let costWeight: Int
    let dailyLimit: Int
    let enabled: Bool
    var id: String { providerId }

    static func displayName(_ providerId: String) -> String {
        switch providerId {
        case ollama: "Local Ollama (on this Mac)"
        case codex: "Codex (ChatGPT plan)"
        case claudeCode: "Claude Code (Claude plan)"
        default: providerId
        }
    }
}

struct ModelProfileData: Decodable { let profile: ModelProfile? }
struct InstalledModelsData: Decodable { let models: [InstalledModel] }
struct ProviderAllowance: Decodable {
    let provider: VerificationProvider
    let startedToday: Int
}

struct ProvidersData: Decodable {
    let providers: [VerificationProvider]
    let discovered: [String: String]
    let allowances: [ProviderAllowance]
}
struct ProviderSavedData: Decodable { let provider: VerificationProvider }
