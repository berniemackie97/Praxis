import AppKit
import SwiftUI

/// One page for every model Praxis uses: connect a provider once (found automatically), then pick
/// the connection, model and reasoning effort for chat and each intake step. Changes save at once.
struct ModelsSettingsView: View {
    @ObservedObject var setup: ModelSetupModel
    @ObservedObject var intelligence: IntelligenceSettingsModel
    @ObservedObject var privacy: PrivacySettingsModel
    @State private var editingAPI: BackendDraft?

    var body: some View {
        Form {
            // Ollama being stopped is shown on its row, not as a page-level error.
            if let error = setup.error ?? privacy.error {
                Banner(text: error, tone: .warning, systemImage: "exclamationmark.triangle.fill")
            } else if let notice = setup.notice {
                Banner(text: notice, tone: .accent, systemImage: "checkmark.circle.fill")
            }
            connections
            roles
            apiConnections
            LocalModelsSection(intelligence: intelligence)
            ActivitySection(privacy: privacy)
        }
        .formStyle(.grouped)
        .sheet(item: $editingAPI) { draft in
            BackendEditor(draft: draft, storedKeys: privacy.storedKeys, removeKey: { privacy.removeKey(account: $0) }) { profile, key in
                privacy.saveBackend(profile, apiKey: key)
                editingAPI = nil
                setup.refresh()
            } cancel: { editingAPI = nil }
        }
        .onAppear {
            setup.refresh()
            intelligence.refresh()
            privacy.refresh()
            intelligence.listModels(endpoint: setup.provider(ModelTool.ollama)?.endpoint ?? "http://127.0.0.1:11434")
        }
    }

    private var connections: some View {
        Section {
            ToolRow(setup: setup, toolId: ModelTool.claudeCode, summary: "Your Claude plan, through Claude Code.",
                    install: "https://docs.anthropic.com/en/docs/claude-code/setup")
            ToolRow(setup: setup, toolId: ModelTool.codex, summary: "Your ChatGPT plan, through Codex.",
                    install: "https://developers.openai.com/codex/cli")
            OllamaRow(setup: setup, intelligence: intelligence)
        } header: {
            HStack {
                Text("Connections")
                Spacer()
                if setup.detecting { ProgressView().controlSize(.small) }
                Button("Check Again") { setup.refresh() }.controlSize(.small).disabled(setup.detecting)
            }
        } footer: {
            Text("Praxis finds these tools on this Mac and uses your plans' own sign-in; it never asks for their passwords. Plans see only what your privacy settings allow.")
                .font(.meta).foregroundStyle(.secondary)
        }
    }

    private var roles: some View {
        Section {
            ForEach(ModelSetupModel.roles, id: \.self) { role in
                RoleRow(setup: setup, role: role, installed: intelligence.installedModels)
            }
        } header: {
            HStack {
                Text("What each model does")
                Spacer()
                Button("Use Recommended") { setup.applyRecommended() }.controlSize(.small)
            }
        } footer: {
            Text("Triage runs on every new file, so a local model keeps it private and free. Reading does the heavy work. Checking runs only when a result fails verification; a different vendor catches different mistakes. Chat sub-agents use the triage model.")
                .font(.meta).foregroundStyle(.secondary)
        }
    }

    private var apiConnections: some View {
        Section {
            ForEach(setup.backends.filter { $0.kind == "openai_compatible" || $0.kind == "anthropic" }.filter { $0.backendId != ModelTool.ollama }) { backend in
                HStack(spacing: Spacing.m) {
                    Image(systemName: backend.isLocal ? "desktopcomputer" : "cloud").foregroundStyle(.secondary).frame(width: 20)
                    VStack(alignment: .leading, spacing: Spacing.xxs) {
                        Text(backend.backendId).font(.rowTitle)
                        Text("\(backend.kindLabel) · \(backend.model)").font(.meta).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if let account = backend.keyAccount {
                        StatusPill(text: privacy.storedKeys.contains(account) ? "Key stored" : "No key",
                                   tone: privacy.storedKeys.contains(account) ? .positive : .warning)
                    }
                    Button("Edit…") { editingAPI = BackendDraft(backend) }.controlSize(.small)
                }
            }
            Button("Add API Key…") { editingAPI = BackendDraft(nil) }
        } header: {
            Text("API keys")
        } footer: {
            Text("Optional, for chat with Anthropic, OpenAI, OpenRouter, DeepSeek, Gemini or a local OpenAI-compatible server. Keys stay in your login Keychain.")
                .font(.meta).foregroundStyle(.secondary)
        }
    }
}

/// Claude Code or Codex: found or not, signed in or not, on or off, and how much it may be used per day.
private struct ToolRow: View {
    @ObservedObject var setup: ModelSetupModel
    let toolId: String
    let summary: String
    let install: String

    private var tool: ModelTool? { setup.tool(toolId) }
    private var provider: VerificationProvider? { setup.provider(toolId) }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            HStack(alignment: .top, spacing: Spacing.m) {
                Image(systemName: "terminal").font(.title3).foregroundStyle(.secondary).frame(width: 24)
                VStack(alignment: .leading, spacing: Spacing.xxs) {
                    HStack(spacing: Spacing.s) {
                        Text(ModelSetupModel.name(toolId)).font(.rowTitle)
                        status
                    }
                    Text(detail).font(.meta).foregroundStyle(.secondary).lineLimit(2).textSelection(.enabled)
                }
                Spacer()
                actions
            }
            if provider?.enabled == true {
                HStack {
                    Spacer().frame(width: 24 + Spacing.m)
                    DailyLimitMenu(limit: provider?.dailyLimit ?? VerificationProvider.unlimited, used: setup.usedToday(toolId)) {
                        setup.setDailyLimit(toolId, $0)
                    }
                    Spacer()
                }
            }
        }
        .padding(.vertical, Spacing.xxs)
    }

    private var detail: String {
        guard let tool, tool.installed, let path = tool.path else { return "Not found on this Mac. \(summary)" }
        let version = tool.version.map { "\($0) · " } ?? ""
        return "\(version)\(path)"
    }

    @ViewBuilder private var status: some View {
        if tool == nil && setup.detecting {
            EmptyView()
        } else if tool?.installed != true {
            StatusPill(text: "Not installed", tone: .neutral)
        } else if tool?.signedIn != true {
            StatusPill(text: "Signed out", tone: .warning)
        } else if provider?.enabled == true {
            StatusPill(text: tool?.account.map { "Connected · \($0)" } ?? "Connected", tone: .positive)
        } else {
            StatusPill(text: "Ready", tone: .neutral)
        }
    }

    @ViewBuilder private var actions: some View {
        HStack(spacing: Spacing.s) {
            if tool?.installed != true {
                Button("Install…") { NSWorkspace.shared.open(URL(string: install)!) }
                Button("Locate…") { browse() }
            } else {
                if tool?.signedIn != true {
                    Button("Sign In…") { setup.signIn(toolId) }
                } else if provider == nil, let path = tool?.path {
                    Button("Connect") { setup.connectCli(toolId, path: path) }
                        .buttonStyle(.borderedProminent)
                        .help("Use \(ModelSetupModel.name(toolId)) for chat and intake")
                }
                if provider != nil {
                    Toggle("Use", isOn: Binding(get: { provider?.enabled == true }, set: { setup.setEnabled(toolId, $0) }))
                        .toggleStyle(.switch).labelsHidden()
                        .help(provider?.enabled == true ? "Turn off for chat and intake" : "Use for chat and intake")
                }
                Menu {
                    Button("Choose Another Copy…") { browse() }
                    if let path = tool?.path {
                        Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)]) }
                    }
                    Button("Sign In Again…") { setup.signIn(toolId) }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).fixedSize()
            }
        }
        .controlSize(.small)
    }

    private func browse() {
        let panel = NSOpenPanel()
        panel.title = "Choose \(ModelSetupModel.name(toolId))"
        panel.message = "Select the \(toolId == ModelTool.codex ? "codex" : "claude") command-line tool."
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.showsHiddenFiles = true
        panel.treatsFilePackagesAsDirectories = true
        panel.directoryURL = URL(fileURLWithPath: tool?.path.map { ($0 as NSString).deletingLastPathComponent } ?? "/opt/homebrew/bin")
        if panel.runModal() == .OK, let url = panel.url {
            setup.locate(toolId, path: url.path)
        }
    }
}

/// The local Ollama model used for private triage and, optionally, private chat.
private struct OllamaRow: View {
    @ObservedObject var setup: ModelSetupModel
    @ObservedObject var intelligence: IntelligenceSettingsModel
    @State private var endpoint = "http://127.0.0.1:11434"

    private var provider: VerificationProvider? { setup.provider(ModelTool.ollama) }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            HStack(alignment: .top, spacing: Spacing.m) {
                Image(systemName: "desktopcomputer").font(.title3).foregroundStyle(.secondary).frame(width: 24)
                VStack(alignment: .leading, spacing: Spacing.xxs) {
                    HStack(spacing: Spacing.s) {
                        Text("Ollama").font(.rowTitle)
                        if provider?.enabled == true {
                            StatusPill(text: "Connected · on this Mac", tone: .positive)
                        } else if !intelligence.installedModels.isEmpty {
                            StatusPill(text: "\(intelligence.installedModels.count) models installed", tone: .neutral)
                        } else {
                            StatusPill(text: setup.tool(ModelTool.ollama)?.installed == true ? "Not running" : "Not installed", tone: .neutral)
                        }
                    }
                    Text(setup.tool(ModelTool.ollama)?.problem == "not_running" && intelligence.installedModels.isEmpty
                         ? "Open the Ollama app to use local models: private and free, slower. Best for triage."
                         : "Local models: private and free, slower. Best for triage.").font(.meta).foregroundStyle(.secondary)
                }
                Spacer()
                Toggle("Use", isOn: Binding(get: { provider?.enabled == true }, set: { on in
                    let model = provider?.model ?? intelligence.installedModels.first?.name ?? ""
                    if !model.isEmpty { setup.connectOllama(endpoint: endpoint, model: model, enabled: on) }
                }))
                .toggleStyle(.switch).labelsHidden().controlSize(.small)
                .disabled(intelligence.installedModels.isEmpty && provider == nil)
            }
            HStack(spacing: Spacing.s) {
                Spacer().frame(width: 24 + Spacing.m)
                TextField("Address", text: $endpoint, prompt: Text("http://127.0.0.1:11434"))
                    .textFieldStyle(.roundedBorder).frame(maxWidth: 220)
                    .onSubmit { intelligence.listModels(endpoint: endpoint) }
                Button("Find Models") { intelligence.listModels(endpoint: endpoint) }.controlSize(.small)
                Spacer()
                if setup.tool(ModelTool.ollama)?.installed != true && intelligence.installedModels.isEmpty {
                    Button("Install…") { NSWorkspace.shared.open(URL(string: "https://ollama.com/download")!) }.controlSize(.small)
                }
            }
        }
        .padding(.vertical, Spacing.xxs)
        .onAppear { if let saved = provider?.endpoint { endpoint = saved } }
    }
}

/// One role: which connection runs it, with which model and how much reasoning.
private struct RoleRow: View {
    @ObservedObject var setup: ModelSetupModel
    let role: String
    let installed: [InstalledModel]

    private var current: (connection: String, model: String, effort: String) { setup.assignment(for: role) }
    private var models: [ModelChoice] { setup.models(for: current.connection, installed: installed) }
    private var efforts: [String] {
        models.first { $0.id == current.model }?.efforts ?? models.first?.efforts ?? []
    }

    var body: some View {
        LabeledContent {
            HStack(spacing: Spacing.s) {
                Picker("Connection", selection: Binding(get: { current.connection }, set: { connection in
                    let first = setup.models(for: connection, installed: installed).first?.id
                    setup.setRole(role, connection: connection, model: connection.isEmpty ? nil : first, effort: nil)
                })) {
                    Text(role == "chat" ? "None" : "Off").tag("")
                    ForEach(setup.connections(for: role), id: \.self) { id in
                        Text(ModelSetupModel.name(id)).tag(id)
                    }
                }
                .labelsHidden().frame(width: 140)
                if !current.connection.isEmpty {
                    Picker("Model", selection: Binding(get: { current.model }, set: { model in
                        setup.setRole(role, connection: current.connection, model: model, effort: current.effort)
                    })) {
                        if !models.contains(where: { $0.id == current.model }) && !current.model.isEmpty {
                            Text(current.model == "default" ? "Plan default" : current.model).tag(current.model)
                        }
                        ForEach(models) { Text($0.label).tag($0.id) }
                    }
                    .labelsHidden().frame(width: 170)
                    if !efforts.isEmpty {
                        Picker("Effort", selection: Binding(get: { current.effort }, set: { effort in
                            setup.setRole(role, connection: current.connection, model: current.model, effort: effort)
                        })) {
                            Text("Default").tag("")
                            ForEach(efforts, id: \.self) { Text($0.capitalized).tag($0) }
                        }
                        .labelsHidden().frame(width: 100)
                    }
                }
            }
        } label: {
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text(Self.title(role))
                Text(Self.subtitle(role)).font(.meta).foregroundStyle(.secondary)
            }
        }
    }

    static func title(_ role: String) -> String {
        switch role {
        case "chat": "Chat"
        case "local": "Triage"
        case "strong": "Reading"
        default: "Checking"
        }
    }

    static func subtitle(_ role: String) -> String {
        switch role {
        case "chat": "Answers you and runs tools"
        case "local": "Sorts every new file"
        case "strong": "Understands documents"
        default: "Fixes failed verification"
        }
    }
}

/// A daily cap for a paid plan, with no limit as the first choice and any number available.
private struct DailyLimitMenu: View {
    let limit: Int
    let used: Int?
    let change: (Int) -> Void
    @State private var custom = false
    @State private var customValue = ""

    private static let presets = [25, 50, 100, 250, 500, 1_000]

    var body: some View {
        HStack(spacing: Spacing.s) {
            Text("Daily limit").font(.meta).foregroundStyle(.secondary)
            Menu(limit == VerificationProvider.unlimited ? "Unlimited" : "\(limit.formatted()) calls") {
                Button("Unlimited") { change(VerificationProvider.unlimited) }
                Divider()
                ForEach(Self.presets, id: \.self) { value in Button("\(value.formatted()) calls") { change(value) } }
                Divider()
                Button("Custom…") {
                    customValue = limit == VerificationProvider.unlimited ? "" : String(limit)
                    custom = true
                }
            }
            .menuStyle(.borderlessButton).fixedSize().font(.meta)
            .popover(isPresented: $custom, arrowEdge: .bottom) {
                HStack {
                    TextField("Calls per day", text: $customValue).frame(width: 120).onSubmit(applyCustom)
                    Button("Set", action: applyCustom).keyboardShortcut(.defaultAction)
                }
                .padding(Spacing.m)
            }
            if let used { Text("· \(used.formatted()) used today").font(.meta).foregroundStyle(.secondary) }
        }
    }

    private func applyCustom() {
        if let value = Int(customValue.filter(\.isNumber)), value >= 1, value <= 100_000 {
            change(value)
            custom = false
        }
    }
}

/// Local enrichment and semantic search, both on this Mac through Ollama.
private struct LocalModelsSection: View {
    @ObservedObject var intelligence: IntelligenceSettingsModel
    @State private var enrichment = ""
    @State private var embedding = ""

    var body: some View {
        Section {
            Picker("Background enrichment", selection: Binding(get: { intelligence.profile?.enabled == true ? intelligence.profile?.model ?? "" : "" }, set: { model in
                let endpoint = intelligence.profile?.endpoint ?? "http://127.0.0.1:11434"
                if model.isEmpty, let profile = intelligence.profile {
                    intelligence.saveProfile(endpoint: profile.endpoint, model: profile.model, contextTokens: profile.contextTokens, enabled: false)
                } else if !model.isEmpty {
                    intelligence.saveProfile(endpoint: endpoint, model: model, contextTokens: intelligence.profile?.contextTokens ?? 8_192, enabled: true)
                }
            })) {
                Text("Off").tag("")
                ForEach(intelligence.installedModels) { Text($0.name).tag($0.name) }
            }
            Picker("Semantic search", selection: Binding(get: { intelligence.embeddingProfile?.enabled == true ? intelligence.embeddingProfile?.model ?? "" : "" }, set: { model in
                let endpoint = intelligence.embeddingProfile?.endpoint ?? "http://127.0.0.1:11434"
                if model.isEmpty, let profile = intelligence.embeddingProfile {
                    intelligence.saveEmbeddingProfile(endpoint: profile.endpoint, model: profile.model, enabled: false)
                } else if !model.isEmpty {
                    intelligence.saveEmbeddingProfile(endpoint: endpoint, model: model, enabled: true)
                }
            })) {
                Text("Off (keyword search)").tag("")
                ForEach(intelligence.installedModels) { Text($0.name).tag($0.name) }
            }
        } header: {
            Text("On this Mac")
        } footer: {
            Text("Enrichment suggests topics and people for new text; semantic search finds passages by meaning. Both run in your Ollama and never leave the Mac.")
                .font(.meta).foregroundStyle(.secondary)
        }
    }
}

/// The latest model calls, for checking what ran where.
private struct ActivitySection: View {
    @ObservedObject var privacy: PrivacySettingsModel
    @State private var expanded = false

    var body: some View {
        Section {
            DisclosureGroup("Recent model calls (\(privacy.invocations.count))", isExpanded: $expanded) {
                if privacy.invocations.isEmpty {
                    Text("No model calls recorded yet.").foregroundStyle(.secondary)
                }
                ForEach(privacy.invocations.prefix(25)) { call in
                    HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
                        Image(systemName: call.remote ? "cloud" : "desktopcomputer").foregroundStyle(.secondary).frame(width: 16)
                        VStack(alignment: .leading, spacing: Spacing.xxs) {
                            Text("\(ModelSetupModel.name(call.providerId)) · \(RoleRow.title(call.tier)) · \(call.purpose)").font(.callout)
                            Text("\(PolicyLabels.category(call.category)) · \(call.outcome) · \(DateText.relative(call.startedUtc))")
                                .font(.meta).foregroundStyle(.secondary)
                        }
                    }
                    .textSelection(.enabled)
                }
            }
        }
    }
}
