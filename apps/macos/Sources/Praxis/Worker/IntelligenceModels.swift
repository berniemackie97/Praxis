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

struct InstalledModel: Decodable, Identifiable, Equatable {
    let name: String
    let digest: String
    let sizeBytes: Int64
    let capabilities: LocalModelCapabilities?
    var id: String { name }
    var supportsChat: Bool { capabilities?.chat ?? Self.mayBeChat(name) }
    var supportsEmbedding: Bool { capabilities?.embedding ?? !Self.mayBeChat(name) }

    static func mayBeChat(_ name: String) -> Bool { !name.localizedCaseInsensitiveContains("embed") }

    static func matching(_ name: String, in installed: [InstalledModel]) -> InstalledModel? {
        installed.first { $0.name == name || $0.name == name + ":latest" }
    }

    static func allowsToolsWithoutMetadata(_ name: String) -> Bool {
        let family = name.split(separator: ":", maxSplits: 1).first ?? ""
        return !["gemma2", "gemma3", "library/gemma2", "library/gemma3"].contains(family)
    }
}

struct LocalModelCapabilities: Decodable, Equatable {
    let chat: Bool
    let tools: Bool
    let embedding: Bool
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
    var effort: String?
    var id: String { providerId }

    /// A daily limit of zero means no cap.
    static let unlimited = 0

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
