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

/// A subscription CLI the owner allows to verify local results.
struct VerificationProvider: Decodable, Identifiable, Equatable {
    static let codex = "codex"
    static let claudeCode = "claude-code"

    let providerId: String
    let executable: String
    let model: String?
    let costWeight: Int
    let dailyLimit: Int
    let enabled: Bool
    var id: String { providerId }

    static func displayName(_ providerId: String) -> String {
        switch providerId {
        case codex: "Codex (ChatGPT plan)"
        case claudeCode: "Claude Code (Claude plan)"
        default: providerId
        }
    }
}

struct ModelProfileData: Decodable { let profile: ModelProfile? }
struct InstalledModelsData: Decodable { let models: [InstalledModel] }
struct ProvidersData: Decodable {
    let providers: [VerificationProvider]
    let discovered: [String: String]
}
struct ProviderSavedData: Decodable { let provider: VerificationProvider }
