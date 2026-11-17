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
        ScrollView {
            VStack(alignment: .leading, spacing: Spacing.xxl) {
                // Ollama being stopped is shown on its row, not as a page-level error.
                if let error = setup.error ?? privacy.error {
                    Banner(text: error, tone: .warning, systemImage: "exclamationmark.triangle.fill")
                } else if let notice = setup.notice {
                    Banner(text: notice, tone: .accent, systemImage: "checkmark.circle.fill")
                }
                FlatSection("Connections",
                            footer: "Praxis finds these tools on this Mac and uses your plans' own sign-in; it never asks for their passwords. Plans see only what your privacy settings allow.") {
                    HStack(spacing: Spacing.s) {
                        if setup.detecting { ProgressView().controlSize(.small) }
                        Button("Check Again") { setup.refresh() }.disabled(setup.detecting)
                    }
                } content: {
                    ToolRow(setup: setup, toolId: ModelTool.claudeCode, summary: "Your Claude plan, through Claude Code.",
                            install: "https://docs.anthropic.com/en/docs/claude-code/setup")
                    Divider()
                    ToolRow(setup: setup, toolId: ModelTool.codex, summary: "Your ChatGPT plan, through Codex.",
                            install: "https://developers.openai.com/codex/cli")
                    Divider()
                    OllamaRow(setup: setup, intelligence: intelligence)
                }
                FlatSection("What each model does",
                            footer: "Triage runs on every new file, so a local model keeps it private and free. Reading does the heavy work, so it runs an efficient model. Checking runs only when a result fails verification. Chat sub-agents use the triage assignment. Changes apply to future calls and remain subject to Privacy permissions.") {
                    Button("Use Recommended") { setup.applyRecommended() }
                } content: {
                    ForEach(Array(ModelSetupModel.roles.enumerated()), id: \.element) { index, role in
                        if index > 0 { Divider() }
                        ModelRoleRow(setup: setup, role: role, installed: intelligence.installedModels)
                    }
                }
                apiConnections
                LocalModelsSection(setup: setup, intelligence: intelligence)
                ActivitySection(privacy: privacy)
            }
            .padding(.horizontal, Spacing.xl)
            .padding(.vertical, Spacing.l)
        }
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

    private var apiConnections: some View {
        FlatSection("API keys",
                    footer: "Optional, for chat with Anthropic, OpenAI, OpenRouter, DeepSeek, Gemini or a local OpenAI-compatible server. Keys stay in your login Keychain.") {
            Button("Add API Key…") { editingAPI = BackendDraft(nil) }
        } content: {
            let apis = setup.backends.filter { ($0.kind == "openai_compatible" || $0.kind == "anthropic") && $0.backendId != ModelTool.ollama }
            if apis.isEmpty {
                Text("None added.").foregroundStyle(.secondary).padding(.vertical, Spacing.xs)
            }
            ForEach(Array(apis.enumerated()), id: \.element.id) { index, backend in
                if index > 0 { Divider() }
                HStack(spacing: Spacing.m) {
                    Image(systemName: backend.isLocal ? "desktopcomputer" : "cloud").foregroundStyle(.secondary).frame(width: 24)
                    VStack(alignment: .leading, spacing: Spacing.xxs) {
                        Text(backend.backendId).font(.rowTitle)
                        Text("\(backend.kindLabel) · \(backend.model)").font(.meta).foregroundStyle(.secondary)
                    }
                    Spacer()
                    if let account = backend.keyAccount {
                        StatusPill(text: privacy.storedKeys.contains(account) ? "Key stored" : "No key",
                                   tone: privacy.storedKeys.contains(account) ? .positive : .warning)
                    }
                    Button("Edit…") { editingAPI = BackendDraft(backend) }
                }
                .padding(.vertical, Spacing.xs)
            }
        }
    }
}

/// A settings section without a card: a header with an optional action, rows separated by hairlines
/// on the window background, and a short footnote.
private struct FlatSection<Trailing: View, Content: View>: View {
    let title: String
    let footer: String?
    @ViewBuilder let trailing: Trailing
    @ViewBuilder let content: Content

    init(_ title: String, footer: String? = nil, @ViewBuilder trailing: () -> Trailing = { EmptyView() }, @ViewBuilder content: () -> Content) {
        self.title = title
        self.footer = footer
        self.trailing = trailing()
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            HStack(alignment: .firstTextBaseline) {
                Text(title).font(.sectionTitle)
                Spacer()
                trailing.controlSize(.small)
            }
            Divider()
            VStack(alignment: .leading, spacing: Spacing.s) { content }
            if let footer {
                Text(footer).font(.meta).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    .padding(.top, Spacing.xxs)
            }
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
struct ModelRoleRow: View {
    @ObservedObject var setup: ModelSetupModel
    let role: String
    let installed: [InstalledModel]

    private var current: (connection: String, model: String, effort: String) { setup.assignment(for: role) }
    private var models: [ModelChoice] { setup.models(for: current.connection, installed: installed) }
    private var efforts: [String] {
        models.first { $0.id == current.model }?.efforts ?? models.first?.efforts ?? []
    }

    var body: some View {
        HStack(alignment: .center, spacing: Spacing.m) {
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text(Self.title(role))
                Text(Self.subtitle(role)).font(.meta).foregroundStyle(.secondary)
            }
            Spacer(minLength: Spacing.m)
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
        }
        .padding(.vertical, Spacing.xxs)
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

/// This Mac's local models: one model kept loaded for every local job, when it drafts, semantic
/// search, and the optional background suggestions, with a one-click setup tuned to the machine.
private struct LocalModelsSection: View {
    @ObservedObject var setup: ModelSetupModel
    @ObservedObject var intelligence: IntelligenceSettingsModel

    private var installed: [InstalledModel] { setup.machine?.installed ?? intelligence.installedModels }
    private var generative: [InstalledModel] { installed.filter(\.supportsChat) }
    private var embeddings: [InstalledModel] { installed.filter(\.supportsEmbedding) }
    private var localModel: String { setup.provider(ModelTool.ollama)?.enabled == true ? setup.provider(ModelTool.ollama)?.model ?? "" : "" }

    var body: some View {
        FlatSection("On this Mac", footer: footer) {
            Button("Optimize for This Mac") { setup.optimizeForThisMac() }
        } content: {
            if let plan = setup.machine?.recommended, !matches(plan) {
                RecommendationRow(plan: plan) { setup.optimizeForThisMac() }
                Divider()
            }
            SettingRow(title: "Local model", detail: localDetail) {
                Picker("Local model", selection: Binding(get: { localModel }, set: { if !$0.isEmpty { setup.setLocalModel($0) } })) {
                    if localModel.isEmpty { Text("None").tag("") }
                    ForEach(generative) { Text("\($0.name.split(separator: "/").last.map(String.init) ?? $0.name)  ·  \(Self.size($0.sizeBytes))").tag($0.name) }
                }
                .labelsHidden().frame(maxWidth: 320)
            }
            Divider()
            SettingRow(title: "Local drafts", detail: draftDetail) {
                Picker("Local drafts", selection: Binding(get: { setup.machine?.draftMode ?? "auto" }, set: { setup.setDraftMode($0) })) {
                    Text("Auto").tag("auto")
                    Text("Always").tag("always")
                    Text("Off").tag("off")
                }
                .pickerStyle(.segmented).labelsHidden().frame(width: 220)
            }
            Divider()
            SettingRow(title: "Semantic search", detail: "Finds passages by meaning. Uses an embedding model, not a chat model.") {
                Picker("Semantic search", selection: Binding(get: { intelligence.embeddingProfile?.enabled == true ? intelligence.embeddingProfile?.model ?? "" : "" }, set: { model in
                    let endpoint = intelligence.embeddingProfile?.endpoint ?? setup.provider(ModelTool.ollama)?.endpoint ?? "http://127.0.0.1:11434"
                    if model.isEmpty, let profile = intelligence.embeddingProfile {
                        intelligence.saveEmbeddingProfile(endpoint: profile.endpoint, model: profile.model, enabled: false)
                    } else if !model.isEmpty {
                        intelligence.saveEmbeddingProfile(endpoint: endpoint, model: model, enabled: true)
                    }
                })) {
                    Text("Off (keyword search)").tag("")
                    ForEach(embeddings) { Text("\($0.name)  ·  \(Self.size($0.sizeBytes))").tag($0.name) }
                }
                .labelsHidden().frame(maxWidth: 320)
            }
            Divider()
            SettingRow(title: "Background suggestions",
                       detail: "The local model suggests topics and people for every chunk of text. Slow on this Mac, and the Reading model already does this.") {
                Toggle("Background suggestions", isOn: Binding(get: { intelligence.profile?.enabled == true }, set: { on in
                    let model = localModel.isEmpty ? (intelligence.profile?.model ?? "") : localModel
                    guard !model.isEmpty else { return }
                    let endpoint = setup.provider(ModelTool.ollama)?.endpoint ?? intelligence.profile?.endpoint ?? "http://127.0.0.1:11434"
                    intelligence.saveProfile(endpoint: endpoint, model: model, contextTokens: intelligence.profile?.contextTokens ?? 8_192, enabled: on)
                }))
                .toggleStyle(.switch).labelsHidden()
            }
        }
        .onAppear { setup.refreshMachine() }
    }

    private var footer: String {
        "Everything here runs in your Ollama and never leaves the Mac. Keep one local model: switching models means reloading several gigabytes."
    }

    private var localDetail: String {
        var parts = ["Sorts every new file, drafts small documents and answers private chats."]
        if let pace = setup.machine?.secondsPerThousandCharacters {
            parts.append(String(format: "Measured here: about %.0f s per page of text.", pace * 2))
        }
        return parts.joined(separator: " ")
    }

    private var draftDetail: String {
        switch setup.machine?.draftMode ?? "auto" {
        case "always": "The local model drafts every small document before the Reading model reviews it. Saves cloud tokens; slow on smaller Macs."
        case "off": "The Reading model reads every document itself. Fastest, uses the most cloud tokens."
        default: "Drafts only when this Mac is measured to finish within \(setup.machine?.draftBudgetSeconds ?? 20) s; otherwise the Reading model reads directly."
        }
    }

    private func matches(_ plan: MachinePlan) -> Bool {
        (plan.localModel == nil || plan.localModel == localModel)
            && (plan.embeddingModel == nil || (intelligence.embeddingProfile?.enabled == true && intelligence.embeddingProfile?.model == plan.embeddingModel))
            && plan.draftMode == setup.machine?.draftMode
            && plan.enrichment == (intelligence.profile?.enabled == true)
    }

    static func size(_ bytes: Int64) -> String { ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file) }
}

/// A labelled setting with an explanation under the label and its control on the right.
private struct SettingRow<Control: View>: View {
    let title: String
    let detail: String
    @ViewBuilder let control: Control

    var body: some View {
        HStack(alignment: .center, spacing: Spacing.l) {
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text(title)
                Text(detail).font(.meta).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            control
        }
        .padding(.vertical, Spacing.xxs)
    }
}

/// What Praxis recommends for this Mac when the current setup differs.
private struct RecommendationRow: View {
    let plan: MachinePlan
    let apply: () -> Void

    private var summary: String {
        let memory = ByteCountFormatter.string(fromByteCount: plan.memoryBytes, countStyle: .memory)
        var parts: [String] = []
        if let local = plan.localModel { parts.append(local.split(separator: "/").last.map(String.init) ?? local) }
        if let embedding = plan.embeddingModel { parts.append("\(embedding) for search") }
        parts.append("drafts \(plan.draftMode)")
        parts.append(plan.enrichment ? "suggestions on" : "suggestions off")
        return "Recommended for this \(memory) Mac: " + parts.joined(separator: ", ") + "."
    }

    var body: some View {
        HStack(alignment: .top, spacing: Spacing.m) {
            Image(systemName: "gauge.with.dots.needle.67percent").foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text(summary).font(.callout)
                ForEach(plan.notes, id: \.self) { Text($0).font(.meta).foregroundStyle(.secondary) }
            }
            Spacer()
            Button("Apply", action: apply).buttonStyle(.borderedProminent).controlSize(.small)
        }
        .padding(.vertical, Spacing.xs)
    }
}

/// The latest model calls, repeats folded together, for checking what ran where.
private struct ActivitySection: View {
    @ObservedObject var privacy: PrivacySettingsModel
    @State private var expanded = false

    private struct Line: Identifiable {
        let id: Int
        let call: ModelInvocation
        var count: Int
    }

    /// Consecutive calls with the same provider, role, purpose and outcome fold into one line.
    private var lines: [Line] {
        var result: [Line] = []
        for (index, call) in privacy.invocations.enumerated() {
            if let last = result.last, last.call.providerId == call.providerId, last.call.tier == call.tier,
               last.call.purpose == call.purpose, last.call.outcome == call.outcome {
                result[result.count - 1].count += 1
            } else {
                result.append(Line(id: index, call: call, count: 1))
            }
        }
        return result
    }

    var body: some View {
        FlatSection("Recent model calls") {
            if lines.count > 6 {
                Button(expanded ? "Show Less" : "Show All") { expanded.toggle() }
            }
        } content: {
            if lines.isEmpty {
                Text("No model calls yet.").foregroundStyle(.secondary)
            }
            ForEach(expanded ? lines : Array(lines.prefix(6))) { line in
                HStack(spacing: Spacing.s) {
                    Image(systemName: line.call.remote ? "cloud" : "desktopcomputer").foregroundStyle(.secondary).frame(width: 18)
                    Text("\(ModelSetupModel.name(line.call.providerId)) · \(RoleRow.title(line.call.tier))").font(.callout)
                    if line.count > 1 { Text("×\(line.count)").font(.meta.monospacedDigit()).foregroundStyle(.secondary) }
                    Text(line.call.outcome.replacingOccurrences(of: "_", with: " "))
                        .font(.meta).foregroundStyle(line.call.outcome == "completed" ? Color.secondary : Color.orange)
                    Spacer()
                    Text(DateText.relative(line.call.startedUtc)).font(.meta).foregroundStyle(.tertiary)
                }
                .textSelection(.enabled)
            }
        }
    }
}
