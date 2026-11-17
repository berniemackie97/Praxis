import AppKit
import Combine
import Foundation

/// The Models settings page: tools found on this Mac, connections, and what each role runs. Every
/// change is saved as soon as it is made.
@MainActor
final class ModelSetupModel: ObservableObject {
    /// Roles in display order. Each intake tier keeps its own provider/model assignment.
    static let roles = ["chat", "local", "strong", "strongest"]

    private let channel: WorkerChannel

    @Published private(set) var tools: [ModelTool] = []
    @Published private(set) var providers: [VerificationProvider] = []
    @Published private(set) var allowances: [ProviderAllowance] = []
    @Published private(set) var backends: [AgentBackendProfile] = []
    @Published private(set) var tiers: [TierAssignment] = []
    @Published private(set) var detecting = false
    @Published private(set) var machine: MachineData?
    @Published private(set) var error: String? { didSet { if error != nil { notice = nil } } }
    @Published private(set) var notice: String? { didSet { if notice != nil { error = nil } } }

    /// The chat model new conversations start with.
    @Published var defaultChat: String = UserDefaults.standard.string(forKey: "defaultChatBackend") ?? "" {
        didSet { UserDefaults.standard.set(defaultChat, forKey: "defaultChatBackend") }
    }

    init(channel: WorkerChannel) {
        self.channel = channel
    }

    func tool(_ id: String) -> ModelTool? { tools.first { $0.id == id } }
    func provider(_ id: String) -> VerificationProvider? { providers.first { $0.providerId == id } }
    func backend(_ id: String) -> AgentBackendProfile? { backends.first { $0.backendId == id } }
    func tier(_ role: String) -> TierAssignment? { tiers.first { $0.tier == role } }
    func usedToday(_ id: String) -> Int? { allowances.first { $0.provider.providerId == id }?.startedToday }

    /// Connections a role can use: enabled intake providers for tiers, enabled chat backends for chat.
    func connections(for role: String) -> [String] {
        let connected = providers.filter(\.enabled).map(\.providerId)
        guard role == "chat" else { return connected }
        // Chat can also use API keys; local Ollama and plan CLIs come from their connections.
        let apis = backends.filter { $0.enabled && ($0.kind == "anthropic" || $0.kind == "openai_compatible") && $0.backendId != ModelTool.ollama }
            .map(\.backendId)
        return connected + apis
    }

    /// The models a connection offers (catalog for plan CLIs, installed models for Ollama, the configured model for APIs).
    func models(for connection: String, installed: [InstalledModel]) -> [ModelChoice] {
        switch connection {
        case ModelTool.claudeCode, ModelTool.codex:
            return tool(connection)?.models ?? [ModelChoice(id: "default", label: "Plan default", efforts: [], defaultEffort: nil)]
        case ModelTool.ollama:
            let names = installed.filter(\.supportsChat).map(\.name)
            let current = provider(connection)?.model.flatMap { name in
                (InstalledModel.matching(name, in: installed)?.supportsChat ?? InstalledModel.mayBeChat(name)) ? [name] : nil
            } ?? []
            return Array(Set(names + current)).sorted().map { ModelChoice(id: $0, label: $0, efforts: [], defaultEffort: nil) }
        default:
            return backend(connection).map { [ModelChoice(id: $0.model, label: $0.model, efforts: [], defaultEffort: nil)] } ?? []
        }
    }

    /// What a role runs now: connection, model and effort.
    func assignment(for role: String) -> (connection: String, model: String, effort: String) {
        if role == "chat" {
            let chosen = backend(defaultChat)?.enabled == true ? defaultChat : (backends.first(where: \.enabled)?.backendId ?? "")
            let profile = backend(chosen)
            return (chosen, profile?.model ?? "", profile?.effort ?? "")
        }
        guard let tier = tier(role) else { return ("", "", "") }
        let provider = provider(tier.providerId)
        return (tier.providerId, tier.model ?? provider?.model ?? "default", tier.effort ?? provider?.effort ?? "")
    }

    func refreshMachine() {
        channel.send("modelMachine", as: MachineData.self, failed: report) { [weak self] data in
            self?.machine = data
        }
    }

    /// Applies the recommended local setup for this Mac.
    func optimizeForThisMac() {
        channel.send("modelMachineApply", as: MachineData.self, failed: report) { [weak self] data in
            guard let self else { return }
            machine = data
            notice = "Optimized for this Mac: one local model stays loaded, drafts run only when they pay off, background suggestions are off."
            refresh()
        }
    }

    /// Makes one model the local model for triage, drafts, private chat and suggestions.
    func setLocalModel(_ name: String) {
        send("localModelSet", ["model": name], success: "\(name) is now the one local model.")
    }

    func setDraftMode(_ mode: String) {
        channel.send("localDraftSet", ["mode": mode], as: IgnoredData.self, failed: report) { [weak self] _ in
            self?.refreshMachine()
        }
    }

    func refresh() {
        detecting = true
        channel.send("modelSetup", as: ModelSetupData.self, failed: { [weak self] message in
            self?.detecting = false
            self?.error = message
        }) { [weak self] data in
            guard let self else { return }
            detecting = false
            tools = data.tools
            providers = data.providers
            allowances = data.allowances
            backends = data.backends
            tiers = data.tiers
        }
        refreshMachine()
    }

    /// Connects (or updates) Claude Code or Codex at a path; it becomes available for chat and intake.
    func connectCli(_ toolId: String, path: String, enabled: Bool = true, dailyLimit: Int? = nil) {
        let limit = dailyLimit ?? provider(toolId)?.dailyLimit ?? VerificationProvider.unlimited
        send("modelConnect", ["toolId": toolId, "executable": path, "enabled": enabled, "dailyLimit": limit],
             success: enabled ? "\(Self.name(toolId)) is connected for chat and intake." : "\(Self.name(toolId)) is turned off.")
    }

    func connectOllama(endpoint: String, model: String, enabled: Bool = true) {
        send("modelConnect", ["toolId": ModelTool.ollama, "endpoint": endpoint, "model": model, "enabled": enabled],
             success: enabled ? "Ollama \(model) is connected." : "Ollama is turned off.")
    }

    func setDailyLimit(_ connection: String, _ limit: Int) {
        guard let provider = provider(connection) else { return }
        if let path = provider.executable {
            connectCli(connection, path: path, enabled: provider.enabled, dailyLimit: limit)
        }
    }

    func setEnabled(_ connection: String, _ enabled: Bool) {
        guard let provider = provider(connection) else {
            if let path = tool(connection)?.path, connection != ModelTool.ollama { connectCli(connection, path: path, enabled: enabled) }
            return
        }
        if connection == ModelTool.ollama, let endpoint = provider.endpoint, let model = provider.model {
            connectOllama(endpoint: endpoint, model: model, enabled: enabled)
        } else if let path = provider.executable {
            connectCli(connection, path: path, enabled: enabled)
        }
    }

    /// Chooses connection, model and effort for a role. An empty connection turns an intake tier off.
    func setRole(_ role: String, connection: String, model: String?, effort: String?) {
        if role == "chat" { defaultChat = connection }
        var fields: [String: Any] = ["role": role]
        if !connection.isEmpty { fields["connectionId"] = connection }
        if let model, !model.isEmpty { fields["model"] = model }
        if let effort, !effort.isEmpty { fields["effort"] = effort }
        send("modelRoleSet", fields, success: nil)
    }

    /// Fills every role with sensible choices from what is connected and signed in.
    func applyRecommended() {
        let claude = provider(ModelTool.claudeCode)?.enabled == true && tool(ModelTool.claudeCode)?.signedIn == true
        let codex = provider(ModelTool.codex)?.enabled == true && tool(ModelTool.codex)?.signedIn == true
        let ollama = provider(ModelTool.ollama)?.enabled == true
        // Spend where it pays: intake reads every file, so it runs the efficient models at low effort;
        // checking only runs on failures and gets one step more. Flagship models stay a manual choice.
        let codexModels = tool(ModelTool.codex)?.models.map(\.id) ?? []
        let codexEfficient = ["luna", "sol"].lazy.compactMap { tier in codexModels.first { $0.contains(tier) } }.first ?? "default"
        if claude {
            setRole("chat", connection: ModelTool.claudeCode, model: "sonnet", effort: "medium")
            setRole("strong", connection: ModelTool.claudeCode, model: "sonnet", effort: "low")
        } else if codex {
            setRole("chat", connection: ModelTool.codex, model: codexEfficient, effort: "medium")
            setRole("strong", connection: ModelTool.codex, model: codexEfficient, effort: "low")
        }
        // A different vendor checks the strong tier's work when both plans are connected.
        if codex && claude {
            setRole("strongest", connection: ModelTool.codex, model: codexEfficient, effort: "medium")
        } else if claude {
            setRole("strongest", connection: ModelTool.claudeCode, model: "sonnet", effort: "high")
        } else if codex {
            setRole("strongest", connection: ModelTool.codex, model: codexEfficient, effort: "high")
        }
        if ollama { setRole("local", connection: ModelTool.ollama, model: nil, effort: nil) }
        notice = claude || codex
            ? "Applied efficient defaults: Sonnet or the lighter Codex models at low to medium effort. Pick Opus or a flagship model per role if you want it."
            : "Connect Claude Code or Codex first."
    }

    /// Inspects a CLI the owner picked and connects it when it responds.
    func locate(_ toolId: String, path: String) {
        channel.send("modelToolInspect", ["toolId": toolId, "path": path], as: ModelToolData.self, failed: report) { [weak self] data in
            guard let self else { return }
            if data.tool.problem == "not_found" || data.tool.version == nil {
                error = "That file is not \(Self.name(toolId))."
                return
            }
            connectCli(toolId, path: path)
        }
    }

    /// Opens Terminal running the tool's own sign-in, so credentials stay with the tool.
    func signIn(_ toolId: String) {
        guard let path = tool(toolId)?.path else { return }
        let command = toolId == ModelTool.codex ? "login" : "auth login"
        let quoted = "'" + path.replacingOccurrences(of: "'", with: "'\\''") + "'"
        let script = "#!/bin/zsh\nclear\necho 'Sign in to \(Self.name(toolId)), then return to Praxis.'\n\(quoted) \(command)\n"
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("praxis-sign-in-\(UUID().uuidString).command")
        do {
            try script.write(to: url, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.path)
            NSWorkspace.shared.open(url)
            notice = "Finish signing in in Terminal, then choose Check Again."
        } catch {
            report("Could not open Terminal to sign in.")
        }
    }

    static func name(_ id: String) -> String {
        switch id {
        case ModelTool.claudeCode: "Claude Code"
        case ModelTool.codex: "Codex"
        case ModelTool.ollama: "Ollama"
        default: id
        }
    }

    private func send(_ action: String, _ fields: [String: Any], success: String?) {
        channel.send(action, fields, as: IgnoredData.self, failed: report) { [weak self] _ in
            if let success { self?.notice = success }
            self?.refresh()
        }
    }

    private func report(_ message: String) { error = message }
}

/// A reply whose content the caller does not need.
struct IgnoredData: Decodable {}
