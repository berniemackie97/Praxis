import Combine
import Foundation

/// The local enrichment model and the subscription CLIs allowed to verify its results.
@MainActor
final class IntelligenceSettingsModel: ObservableObject {
    private let channel: WorkerChannel

    @Published private(set) var profile: ModelProfile?
    @Published private(set) var embeddingProfile: ModelProfile?
    @Published private(set) var installedModels: [InstalledModel] = []
    @Published private(set) var providers: [VerificationProvider] = []
    @Published private(set) var discovered: [String: String] = [:]
    /// The latest failure or confirmation; setting one clears the other.
    @Published private(set) var error: String? { didSet { if error != nil { notice = nil } } }
    @Published private(set) var notice: String? { didSet { if notice != nil { error = nil } } }

    init(channel: WorkerChannel) {
        self.channel = channel
    }

    func refresh() {
        channel.send("modelProfile", as: ModelProfileData.self, failed: report) { [weak self] data in
            self?.profile = data.profile
        }
        channel.send("embeddingProfile", as: ModelProfileData.self, failed: report) { [weak self] data in
            self?.embeddingProfile = data.profile
        }
        channel.send("verificationProviders", as: ProvidersData.self, failed: report) { [weak self] data in
            self?.providers = data.providers
            self?.discovered = data.discovered
        }
    }

    func listModels(endpoint: String) {
        installedModels = []
        channel.send("localModels", ["endpoint": endpoint], as: InstalledModelsData.self, failed: report) { [weak self] data in
            self?.installedModels = data.models
            self?.notice = data.models.isEmpty ? "The runtime has no installed models." : nil
        }
    }

    func saveProfile(endpoint: String, model: String, contextTokens: Int, enabled: Bool) {
        channel.send("modelProfileSave", [
            "provider": "ollama", "endpoint": endpoint, "model": model,
            "contextTokens": contextTokens, "enabled": enabled
        ], as: ModelProfileData.self, failed: report) { [weak self] data in
            self?.profile = data.profile
            self?.notice = enabled ? "Local model saved. New text is enriched in the background." : "Local enrichment is off."
        }
    }

    func saveEmbeddingProfile(endpoint: String, model: String, enabled: Bool) {
        channel.send("embeddingProfileSave", [
            "provider": "ollama", "endpoint": endpoint, "model": model,
            "contextTokens": 2_048, "enabled": enabled
        ], as: ModelProfileData.self, failed: report) { [weak self] data in
            self?.embeddingProfile = data.profile
            self?.notice = enabled ? "Local embedding model saved. Index your knowledge from Search to use hybrid retrieval." : "Semantic retrieval is off; keyword search remains available."
        }
    }

    func saveProvider(
        _ providerId: String, executable: String, model: String, costWeight: Int, dailyLimit: Int, enabled: Bool
    ) {
        var fields: [String: Any] = [
            "providerId": providerId, "executable": executable,
            "costWeight": costWeight, "dailyLimit": dailyLimit, "enabled": enabled
        ]
        let trimmed = model.trimmingCharacters(in: .whitespaces)
        if !trimmed.isEmpty { fields["model"] = trimmed }
        channel.send("verificationProviderSave", fields, as: ProviderSavedData.self, failed: report) { [weak self] _ in
            self?.notice = "\(VerificationProvider.displayName(providerId)) saved. Waiting requests are retried."
            self?.refresh()
        }
    }

    private func report(_ message: String) {
        error = message
    }
}
