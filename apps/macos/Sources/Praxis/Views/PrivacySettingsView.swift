import SwiftUI

/// What may leave this Mac, what the agent may change, which models fill each tier, and which
/// chat models are available — with the record of recent model calls.
struct PrivacySettingsView: View {
    @ObservedObject var model: PrivacySettingsModel
    @ObservedObject var intelligence: IntelligenceSettingsModel

    private var providers: [VerificationProvider] { intelligence.providers }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                if let policy = model.policy {
                    ModeSettings(model: model, policy: policy)
                    Divider()
                    RuleSettings(model: model, policy: policy, providerIds: providerIds)
                    Divider()
                    FallbackSettings(model: model, policy: policy)
                    Divider()
                }
                TierSettings(model: model, providers: providers)
                Divider()
                BackendSettings(model: model)
                Divider()
                InvocationAudit(invocations: model.invocations)
                FeedbackLine(error: model.error, notice: model.notice)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .onAppear {
            model.refresh()
            intelligence.refresh()
        }
    }

    private var providerIds: [String] {
        Array(Set(providers.map(\.providerId) + model.backends.map(\.backendId))).sorted()
    }
}

private struct ModeSettings: View {
    @ObservedObject var model: PrivacySettingsModel
    let policy: DisclosurePolicy

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("What may leave this Mac").font(.headline)
            Picker("Mode", selection: Binding(get: { policy.mode }, set: { model.setMode($0) })) {
                ForEach(DisclosurePolicy.modes, id: \.self) { Text(PolicyLabels.mode($0)).tag($0) }
            }
            .pickerStyle(.radioGroup)
            Picker("Agent may", selection: Binding(get: { policy.toolPermission }, set: { model.setToolPermission($0) })) {
                ForEach(DisclosurePolicy.toolPermissions, id: \.self) { Text(PolicyLabels.toolPermission($0)).tag($0) }
            }
            .frame(maxWidth: 360)
            Text("Files you pin as local-only never leave, whatever the mode. Chat changes that widen any of this always need your confirmation in Approvals.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}

private struct RuleSettings: View {
    @ObservedObject var model: PrivacySettingsModel
    let policy: DisclosurePolicy
    let providerIds: [String]
    @State private var category = "financial"
    @State private var provider = ""
    @State private var purpose = "intake"
    @State private var allow = true
    @State private var days = 0

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Grants and denials").font(.headline)
            Text("A denial always wins. Grants matter in Standing grants mode.").font(.caption).foregroundStyle(.secondary)
            ForEach(policy.rules) { rule in
                HStack {
                    Image(systemName: rule.allow ? "checkmark.circle" : "xmark.octagon").foregroundColor(rule.allow ? .green : .red)
                    Text("\(PolicyLabels.category(rule.category)) → \(rule.providerId) for \(rule.purpose)")
                    if let expires = rule.expiresUtc { Text("until \(DateText.short(expires))").font(.caption).foregroundStyle(.secondary) }
                    Spacer()
                    Button("Remove") { model.removeRule(rule) }.controlSize(.small)
                }
            }
            HStack {
                Picker("", selection: $allow) { Text("Allow").tag(true); Text("Deny").tag(false) }.frame(width: 90)
                Picker("", selection: $category) {
                    ForEach(DisclosurePolicy.categories, id: \.self) { Text(PolicyLabels.category($0)).tag($0) }
                }.frame(width: 150)
                Text("→")
                Picker("", selection: $provider) {
                    Text("Provider").tag("")
                    ForEach(providerIds, id: \.self) { Text($0).tag($0) }
                }.frame(width: 160)
                Picker("", selection: $purpose) { Text("Intake").tag("intake"); Text("Chat").tag("chat") }.frame(width: 90)
                Stepper(days == 0 ? "No expiry" : "\(days) days", value: $days, in: 0...365, step: 7).frame(width: 140)
                Button("Add") { model.addRule(category: category, providerId: provider, purpose: purpose, allow: allow, expiresInDays: days) }
                    .disabled(provider.isEmpty)
            }
        }
    }
}

private struct FallbackSettings: View {
    @ObservedObject var model: PrivacySettingsModel
    let policy: DisclosurePolicy
    @State private var category = "financial"

    var body: some View {
        let chain = policy.fallbacks(for: category)
        VStack(alignment: .leading, spacing: 8) {
            Text("When a remote model isn't allowed").font(.headline)
            Picker("Category", selection: $category) {
                ForEach(DisclosurePolicy.categories, id: \.self) { Text(PolicyLabels.category($0)).tag($0) }
            }
            .frame(maxWidth: 260)
            ForEach(DisclosurePolicy.fallbackSteps, id: \.self) { step in
                Toggle(PolicyLabels.fallback(step), isOn: Binding(get: { chain.contains(step) }, set: { enabled in
                    model.setFallbacks(category: category, chain: FallbackChain.toggled(chain, step: step, enabled: enabled))
                }))
            }
            Text("Steps run in this order. Held items continue down the chain when their deadline passes.").font(.caption).foregroundStyle(.secondary)
        }
    }
}

private struct TierSettings: View {
    @ObservedObject var model: PrivacySettingsModel
    let providers: [VerificationProvider]

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Intake model tiers").font(.headline)
            Text("Local triages only. Strong does the heavy reading. Strongest is asked only when a result fails verification. Providers are configured under Models.")
                .font(.caption).foregroundStyle(.secondary)
            ForEach(PrivacySettingsModel.tiers, id: \.self) { tier in
                Picker(tier.capitalized, selection: Binding(get: { model.provider(for: tier) }, set: { model.setTier(tier, providerId: $0) })) {
                    Text("Off").tag("")
                    ForEach(providers.filter(\.enabled)) { Text(VerificationProvider.displayName($0.providerId)).tag($0.providerId) }
                }
                .frame(maxWidth: 360)
            }
        }
    }
}

private struct BackendSettings: View {
    @ObservedObject var model: PrivacySettingsModel
    @State private var backendId = ""
    @State private var kind = "openai_compatible"
    @State private var endpoint = "http://127.0.0.1:11434/v1"
    @State private var modelName = ""
    @State private var keyAccount = ""
    @State private var executable = ""
    @State private var apiKey = ""
    @State private var enabled = true

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Chat models").font(.headline)
            ForEach(model.backends) { backend in
                HStack {
                    Text(backend.backendId).fontWeight(.medium)
                    Text("\(backend.kindLabel) · \(backend.model)\(backend.enabled ? "" : " · off")").foregroundStyle(.secondary)
                    if let account = backend.keyAccount {
                        Text(model.storedKeys.contains(account) ? "key stored" : "no key").font(.caption)
                            .foregroundColor(model.storedKeys.contains(account) ? .green : .orange)
                    }
                    Spacer()
                    Button("Edit") { load(backend) }.controlSize(.small)
                }
            }
            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow {
                    Text("Name")
                    TextField("e.g. claude-api", text: $backendId)
                }
                GridRow {
                    Text("Kind")
                    Picker("", selection: $kind) {
                        Text("OpenAI-compatible (OpenAI, DeepSeek, OpenRouter, Ollama, LM Studio)").tag("openai_compatible")
                        Text("Anthropic API").tag("anthropic")
                        Text("Claude Code (your plan)").tag("claude_code")
                        Text("Codex (your plan)").tag("codex")
                    }
                }
                if ["openai_compatible", "anthropic"].contains(kind) {
                    GridRow {
                        Text("Endpoint")
                        TextField(kind == "anthropic" ? "https://api.anthropic.com" : "https://api.openai.com/v1", text: $endpoint)
                    }
                    GridRow {
                        Text("API key")
                        HStack {
                            TextField("Keychain account, e.g. anthropic", text: $keyAccount).frame(width: 220)
                            SecureField("Paste key to store in Keychain", text: $apiKey)
                            if model.storedKeys.contains(keyAccount) { Button("Remove key") { model.removeKey(account: keyAccount) } }
                        }
                    }
                } else {
                    GridRow {
                        Text("Executable")
                        TextField("/opt/homebrew/bin/claude", text: $executable)
                    }
                }
                GridRow {
                    Text("Model")
                    TextField("Model identifier", text: $modelName)
                }
                GridRow {
                    Text("")
                    Toggle("Enabled", isOn: $enabled)
                }
            }
            HStack {
                Button("Save") {
                    model.saveBackend(AgentBackendProfile(backendId: backendId, kind: kind,
                        endpoint: ["openai_compatible", "anthropic"].contains(kind) ? endpoint : nil, model: modelName,
                        keyAccount: keyAccount.isEmpty ? nil : keyAccount, executable: executable.isEmpty ? nil : executable, enabled: enabled), apiKey: apiKey)
                    apiKey = ""
                }
                .disabled(backendId.isEmpty || modelName.isEmpty)
                Text("Keys go to your login Keychain (service “Praxis”), never to the catalog.").font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private func load(_ backend: AgentBackendProfile) {
        backendId = backend.backendId
        kind = backend.kind
        endpoint = backend.endpoint ?? ""
        modelName = backend.model
        keyAccount = backend.keyAccount ?? ""
        executable = backend.executable ?? ""
        enabled = backend.enabled
        apiKey = ""
    }
}

private struct InvocationAudit: View {
    let invocations: [ModelInvocation]

    var body: some View {
        DisclosureGroup("Recent model calls (\(invocations.count))") {
            ForEach(invocations) { call in
                Text("\(DateText.short(call.startedUtc)) · \(call.tier) · \(call.providerId)\(call.remote ? " (remote)" : "") · \(PolicyLabels.category(call.category)) · \(call.purpose) · \(call.outcome)")
                    .font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            }
        }
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
