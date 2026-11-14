import SwiftUI

/// What may leave this Mac and what the agent may change.
struct PrivacySettingsView: View {
    @ObservedObject var model: PrivacySettingsModel
    @ObservedObject var intelligence: IntelligenceSettingsModel
    @State private var addingRule = false
    @State private var fallbackCategory = "financial"

    private var providerIds: [String] {
        Array(Set(intelligence.providers.map(\.providerId) + model.backends.map(\.backendId))).sorted()
    }

    var body: some View {
        ScrollView {
        Form {
            if let error = model.error ?? model.notice {
                Banner(text: error, tone: model.error == nil ? .accent : .warning,
                       systemImage: model.error == nil ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
            }
            if model.policy == nil && model.error == nil {
                Section {
                    HStack(spacing: Spacing.s) {
                        ProgressView().controlSize(.small)
                        Text("Loading your disclosure policy…").foregroundStyle(.secondary)
                    }
                }
            }
            if model.policy != nil {
                LinkSettingsSection()
            }
            if let policy = model.policy {
                Section {
                    Picker("Remote processing", selection: Binding(get: { policy.mode }, set: { model.setMode($0) })) {
                        ForEach(DisclosurePolicy.modes, id: \.self) { Text(PolicyLabels.mode($0)).tag($0) }
                    }
                    .pickerStyle(.radioGroup)
                    Picker("The agent may", selection: Binding(get: { policy.toolPermission }, set: { model.setToolPermission($0) })) {
                        ForEach(DisclosurePolicy.toolPermissions, id: \.self) { Text(PolicyLabels.toolPermission($0)).tag($0) }
                    }
                } header: {
                    Text("What may leave this Mac").font(.sectionTitle).frame(maxWidth: .infinity, alignment: .leading).padding(.top, Spacing.l)
                } footer: {
                    Text("Files you pin as local-only never leave, whatever the mode. Changes requested in chat that widen any of this always wait for you in Approvals.")
                        .font(.meta).foregroundStyle(.secondary)
                }

                Section {
                    if policy.rules.isEmpty {
                        Text("No grants or denials yet.").foregroundStyle(.secondary)
                    }
                    ForEach(policy.rules) { rule in
                        HStack(spacing: Spacing.s) {
                            Image(systemName: rule.allow ? "checkmark.circle.fill" : "xmark.octagon.fill")
                                .foregroundStyle(rule.allow ? Color.green : Color.red)
                            VStack(alignment: .leading, spacing: Spacing.xxs) {
                                Text("\(rule.allow ? "Allow" : "Deny") \(PolicyLabels.category(rule.category)) → \(rule.providerId)")
                                Text("for \(rule.purpose)" + (rule.expiresUtc.map { " · until \(DateText.short($0))" } ?? ""))
                                    .font(.meta).foregroundStyle(.secondary)
                            }
                            Spacer()
                            Button { model.removeRule(rule) } label: { Image(systemName: "minus.circle") }
                                .buttonStyle(.borderless)
                                .help("Remove rule")
                                .accessibilityLabel("Remove rule")
                        }
                    }
                    Button("Add Rule…") { addingRule = true }
                } header: {
                    Text("Grants and denials").font(.sectionTitle).frame(maxWidth: .infinity, alignment: .leading).padding(.top, Spacing.l)
                } footer: {
                    Text("A denial always wins. Grants apply in Standing grants mode.").font(.meta).foregroundStyle(.secondary)
                }

                Section {
                    Picker("Category", selection: $fallbackCategory) {
                        ForEach(DisclosurePolicy.categories, id: \.self) { Text(PolicyLabels.category($0)).tag($0) }
                    }
                    let chain = policy.fallbacks(for: fallbackCategory)
                    ForEach(DisclosurePolicy.fallbackSteps, id: \.self) { step in
                        Toggle(PolicyLabels.fallback(step), isOn: Binding(get: { chain.contains(step) }, set: { enabled in
                            model.setFallbacks(category: fallbackCategory, chain: FallbackChain.toggled(chain, step: step, enabled: enabled))
                        }))
                    }
                } header: {
                    Text("When a remote model isn't allowed").font(.sectionTitle).frame(maxWidth: .infinity, alignment: .leading).padding(.top, Spacing.l)
                } footer: {
                    Text("Steps run in this order. Held items continue down the chain when their deadline passes, so nothing waits forever.")
                        .font(.meta).foregroundStyle(.secondary)
                }
            }
        }
        .formStyle(.columns)
        .padding(.horizontal, Spacing.xl).padding(.vertical, Spacing.l)
        }
        .sheet(isPresented: $addingRule) {
            RuleEditor(providerIds: providerIds) { category, provider, purpose, allow, days in
                model.addRule(category: category, providerId: provider, purpose: purpose, allow: allow, expiresInDays: days)
                addingRule = false
            } cancel: { addingRule = false }
        }
        .onAppear {
            model.refresh()
            intelligence.refresh()
        }
    }
}

private struct RuleEditor: View {
    let providerIds: [String]
    let save: (String, String, String, Bool, Int) -> Void
    let cancel: () -> Void
    @State private var category = "financial"
    @State private var provider = ""
    @State private var purpose = "intake"
    @State private var allow = true
    @State private var days = 0

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Picker("Rule", selection: $allow) {
                    Text("Allow").tag(true)
                    Text("Deny").tag(false)
                }
                .pickerStyle(.segmented)
                Picker("Category", selection: $category) {
                    ForEach(DisclosurePolicy.categories, id: \.self) { Text(PolicyLabels.category($0)).tag($0) }
                }
                Picker("Provider", selection: $provider) {
                    Text("Choose…").tag("")
                    ForEach(providerIds, id: \.self) { Text($0).tag($0) }
                }
                Picker("Used for", selection: $purpose) {
                    Text("Intake").tag("intake")
                    Text("Chat").tag("chat")
                }
                .pickerStyle(.segmented)
                Stepper(days == 0 ? "Never expires" : "Expires in \(days) days", value: $days, in: 0...365, step: 7)
            }
            .formStyle(.grouped)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel, action: cancel).keyboardShortcut(.cancelAction)
                Button("Add Rule") { save(category, provider, purpose, allow, days) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(provider.isEmpty)
            }
            .padding(Spacing.l)
        }
        .frame(width: 440)
    }
}

/// Intake model tiers, chat models and the record of recent model calls.
struct BackendDraft: Identifiable {
    let existing: AgentBackendProfile?
    var id: String { existing?.backendId ?? "new" }
    init(_ existing: AgentBackendProfile?) { self.existing = existing }
}

struct BackendEditor: View {
    let draft: BackendDraft
    let storedKeys: Set<String>
    let removeKey: (String) -> Void
    let save: (AgentBackendProfile, String) -> Void
    let cancel: () -> Void
    @State private var backendId = ""
    @State private var kind = "openai_compatible"
    @State private var endpoint = "http://127.0.0.1:11434/v1"
    @State private var modelName = ""
    @State private var keyAccount = ""
    @State private var executable = ""
    @State private var apiKey = ""
    @State private var enabled = true

    private var usesEndpoint: Bool { ["openai_compatible", "anthropic"].contains(kind) }

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    TextField("Name", text: $backendId, prompt: Text("e.g. claude-api"))
                        .disabled(draft.existing != nil)
                    Picker("Kind", selection: $kind) {
                        Text("OpenAI-compatible").tag("openai_compatible")
                        Text("Anthropic API").tag("anthropic")
                        Text("Claude Code (your plan)").tag("claude_code")
                        Text("Codex (your plan)").tag("codex")
                    }
                    TextField("Model", text: $modelName, prompt: Text("Model identifier"))
                    Toggle("Enabled", isOn: $enabled)
                } footer: {
                    if kind == "openai_compatible" {
                        Text("OpenAI, DeepSeek, OpenRouter, Gemini's compatible endpoint, or a local Ollama / LM Studio server.")
                            .font(.meta).foregroundStyle(.secondary)
                    }
                }
                if usesEndpoint {
                    Section {
                        TextField("Endpoint", text: $endpoint,
                                  prompt: Text(kind == "anthropic" ? "https://api.anthropic.com" : "https://api.openai.com/v1"))
                        TextField("Keychain account", text: $keyAccount, prompt: Text("e.g. anthropic"))
                        SecureField("API key", text: $apiKey, prompt: Text(storedKeys.contains(keyAccount) ? "Stored — paste to replace" : "Paste key"))
                        if storedKeys.contains(keyAccount) {
                            Button("Remove Stored Key", role: .destructive) { removeKey(keyAccount) }
                        }
                    } header: {
                        Text("Connection")
                    }
                } else {
                    Section {
                        TextField("Executable", text: $executable, prompt: Text(kind == "codex" ? "/opt/homebrew/bin/codex" : "/opt/homebrew/bin/claude"))
                    } header: {
                        Text("Command-line tool")
                    } footer: {
                        Text("Runs headless with its own tools disabled; it reaches your sources only through Praxis.")
                            .font(.meta).foregroundStyle(.secondary)
                    }
                }
            }
            .formStyle(.grouped)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel, action: cancel).keyboardShortcut(.cancelAction)
                Button("Save") {
                    save(AgentBackendProfile(backendId: backendId, kind: kind,
                                             endpoint: usesEndpoint ? endpoint : nil, model: modelName,
                                             keyAccount: keyAccount.isEmpty ? nil : keyAccount,
                                             executable: executable.isEmpty ? nil : executable, enabled: enabled), apiKey)
                    apiKey = ""
                }
                .keyboardShortcut(.defaultAction)
                .disabled(backendId.isEmpty || modelName.isEmpty)
            }
            .padding(Spacing.l)
        }
        .frame(width: 480, height: 520)
        .onAppear(perform: load)
    }

    private func load() {
        guard let backend = draft.existing else { return }
        backendId = backend.backendId
        kind = backend.kind
        endpoint = backend.endpoint ?? ""
        modelName = backend.model
        keyAccount = backend.keyAccount ?? ""
        executable = backend.executable ?? ""
        enabled = backend.enabled
    }
}

/// Keeps a fallback chain in canonical order and always ending in a terminal step.
enum FallbackChain {
    static func toggled(_ chain: [String], step: String, enabled: Bool) -> [String] {
        var next = DisclosurePolicy.fallbackSteps.filter { $0 == step ? enabled : chain.contains($0) }
        if !next.contains("index_only") && !next.contains("skip") { next.append("index_only") }
        return next
    }
}
