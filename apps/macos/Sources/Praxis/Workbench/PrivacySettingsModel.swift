import Combine
import Foundation

/// What may leave this Mac and which models do what: disclosure mode, grants and denials,
/// fallback chains, agent tool permission, model tiers, chat backends with Keychain keys, and
/// the audit of recent model calls. Changes made here are the owner's and apply immediately.
@MainActor
final class PrivacySettingsModel: ObservableObject {
    static let tiers = ["local", "strong", "strongest"]

    private let channel: WorkerChannel
    private let keychain: KeychainStore

    @Published private(set) var policy: DisclosurePolicy?
    @Published private(set) var tierAssignments: [TierAssignment] = []
    @Published private(set) var backends: [AgentBackendProfile] = []
    @Published private(set) var invocations: [ModelInvocation] = []
    @Published private(set) var storedKeys: Set<String> = []
    @Published private(set) var error: String? { didSet { if error != nil { notice = nil } } }
    @Published private(set) var notice: String? { didSet { if notice != nil { error = nil } } }

    init(channel: WorkerChannel, keychain: KeychainStore = KeychainStore()) {
        self.channel = channel
        self.keychain = keychain
    }

    /// Hands every configured backend's Keychain key to the worker (memory only). Called when the
    /// worker starts; a missing key is simply skipped and reported when that backend is used.
    func syncSecrets() {
        channel.send("agentBackends", as: AgentBackendsData.self, failed: report) { [weak self] data in
            guard let self else { return }
            for account in Set(data.backends.compactMap(\.keyAccount)) {
                if let secret = keychain.read(account) { push(secret, account: account) }
            }
        }
    }

    func provider(for tier: String) -> String { tierAssignments.first { $0.tier == tier }?.providerId ?? "" }

    func refresh() {
        channel.send("policyGet", as: PolicyData.self, failed: report) { [weak self] data in
            self?.policy = data.policy
            self?.tierAssignments = data.tiers
        }
        channel.send("agentBackends", as: AgentBackendsData.self, failed: report) { [weak self] data in
            guard let self else { return }
            backends = data.backends
            storedKeys = Set(data.backends.compactMap(\.keyAccount).filter { self.keychain.contains($0) })
        }
        channel.send("modelInvocations", ["limit": 50], as: ModelInvocationsData.self, failed: report) { [weak self] data in
            self?.invocations = data.invocations
        }
    }

    func setMode(_ mode: String) { change(["change": "mode", "mode": mode], "Disclosure mode updated.") }

    func setToolPermission(_ permission: String) {
        change(["change": "toolPermission", "toolPermission": permission], "Agent permission updated.")
    }

    func addRule(category: String, providerId: String, purpose: String, allow: Bool, expiresInDays: Int?) {
        let provider = providerId.trimmingCharacters(in: .whitespacesAndNewlines)
        guard DisclosurePolicy.categories.contains(category), !provider.isEmpty, provider.count <= 64 else {
            error = "Choose a category and a provider."
            return
        }

        var fields: [String: Any] = ["change": "addRule", "category": category, "providerId": provider, "purpose": purpose, "allow": allow]
        if let expiresInDays, expiresInDays > 0 {
            fields["expiresUtc"] = ISO8601DateFormatter().string(from: Date().addingTimeInterval(Double(expiresInDays) * 86_400))
        }
        change(fields, allow ? "Grant added." : "Denial added.")
    }

    func removeRule(_ rule: DisclosureRule) { change(["change": "removeRule", "ruleId": rule.ruleId], "Rule removed.") }

    func setFallbacks(category: String, chain: [String]) {
        guard let last = chain.last, ["index_only", "skip"].contains(last) else {
            error = "A fallback chain must end with “Keep searchable only” or “Skip”."
            return
        }
        change(["change": "fallback", "category": category, "chain": chain.joined(separator: ",")], "Fallbacks updated.")
    }

    func setTier(_ tier: String, providerId: String) {
        var fields: [String: Any] = ["tier": tier]
        if !providerId.isEmpty { fields["providerId"] = providerId }
        channel.send("modelTierSet", fields, as: ChangedFlag.self, failed: report) { [weak self] _ in
            self?.notice = providerId.isEmpty ? "The \(tier) tier is off." : "The \(tier) tier uses \(providerId)."
            self?.refresh()
        }
    }

    func saveBackend(_ profile: AgentBackendProfile, apiKey: String) {
        let key = apiKey.trimmingCharacters(in: .whitespacesAndNewlines)
        if !key.isEmpty {
            guard let account = profile.keyAccount, !account.isEmpty else {
                error = "Name the Keychain account to store the API key under."
                return
            }
            do {
                try keychain.save(key, account: account)
            } catch {
                self.error = "The API key could not be saved to the Keychain (\(error.localizedDescription))."
                return
            }
            push(key, account: account)
        }

        var fields: [String: Any] = ["backendId": profile.backendId, "kind": profile.kind, "model": profile.model, "enabled": profile.enabled]
        if let endpoint = profile.endpoint, !endpoint.isEmpty { fields["endpoint"] = endpoint }
        if let account = profile.keyAccount, !account.isEmpty { fields["keyAccount"] = account }
        if let executable = profile.executable, !executable.isEmpty { fields["executable"] = executable }
        channel.send("agentBackendSave", fields, as: ChangedFlag.self, failed: report) { [weak self] _ in
            self?.notice = "Saved \(profile.backendId)."
            self?.refresh()
        }
    }

    func removeKey(account: String) {
        keychain.delete(account: account)
        storedKeys.remove(account)
        channel.send("agentSecretClear", ["account": account], as: ChangedFlag.self, failed: report) { [weak self] _ in
            self?.notice = "API key removed from the Keychain and from the running worker."
        }
    }

    private func push(_ secret: String, account: String) {
        channel.send("agentSecretSet", ["account": account, "secret": secret], as: ChangedFlag.self, failed: report) { _ in }
    }

    private func change(_ fields: [String: Any], _ success: String) {
        channel.send("policyChange", fields, as: PolicyChangeData.self, failed: report) { [weak self] data in
            self?.notice = data.applied ? success : "Waiting for confirmation."
            self?.refresh()
        }
    }

    private func report(_ message: String) { error = message }
}
