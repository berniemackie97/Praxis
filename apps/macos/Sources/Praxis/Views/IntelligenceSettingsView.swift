import SwiftUI

/// The local enrichment model and the subscription CLIs that may verify its results.
struct IntelligenceSettingsView: View {
    @ObservedObject var model: IntelligenceSettingsModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                LocalModelSettings(model: model)
                Divider()
                VStack(alignment: .leading, spacing: 6) {
                    Text("Stronger-model verification").font(.headline)
                    Text("Praxis runs your own signed-in CLIs, never API keys. Each request waits for your approval. Lower cost weight is tried first; the daily limit caps requests per provider.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                ProviderSettings(model: model, providerId: VerificationProvider.codex, defaultWeight: 10,
                                 modelHint: "Leave empty for the plan default")
                ProviderSettings(model: model, providerId: VerificationProvider.claudeCode, defaultWeight: 50,
                                 modelHint: "sonnet, haiku or opus")
                FeedbackLine(error: model.error, notice: model.notice)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .onAppear { model.refresh() }
    }
}

private struct LocalModelSettings: View {
    @ObservedObject var model: IntelligenceSettingsModel
    @State private var endpoint = "http://127.0.0.1:11434"
    @State private var modelName = ""
    @State private var contextTokens = 8_192
    @State private var enabled = true

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Local enrichment model").font(.headline)
            Text("Runs on this Mac through Ollama. It suggests topics, entities and relations for every captured text; nothing leaves the Mac.")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                TextField("Ollama address", text: $endpoint).textFieldStyle(.roundedBorder).frame(width: 240)
                Button("List installed models") { model.listModels(endpoint: endpoint) }
            }
            HStack {
                Picker("Model", selection: $modelName) {
                    if modelName.isEmpty { Text("Choose a model").tag("") }
                    if !modelName.isEmpty && !model.installedModels.contains(where: { $0.name == modelName }) {
                        Text(modelName).tag(modelName)
                    }
                    ForEach(model.installedModels) { installed in
                        Text("\(installed.name) (\(ByteCountFormatter.string(fromByteCount: installed.sizeBytes, countStyle: .file)))")
                            .tag(installed.name)
                    }
                }
                .frame(maxWidth: 420)
                Stepper("Context \(contextTokens) tokens", value: $contextTokens, in: 2_048...32_768, step: 2_048)
            }
            HStack {
                Toggle("Enrich new and existing text", isOn: $enabled)
                Spacer()
                Button("Save local model") {
                    model.saveProfile(endpoint: endpoint, model: modelName, contextTokens: contextTokens, enabled: enabled)
                }
                .disabled(modelName.isEmpty)
            }
        }
        .onAppear(perform: load)
        .onChange(of: model.profile) { _ in load() }
    }

    private func load() {
        guard let profile = model.profile else { return }
        endpoint = profile.endpoint.hasSuffix("/") ? String(profile.endpoint.dropLast()) : profile.endpoint
        modelName = profile.model
        contextTokens = profile.contextTokens
        enabled = profile.enabled
    }
}

private struct ProviderSettings: View {
    @ObservedObject var model: IntelligenceSettingsModel
    let providerId: String
    let defaultWeight: Int
    let modelHint: String
    @State private var executable = ""
    @State private var modelName = ""
    @State private var costWeight = 10
    @State private var dailyLimit = 20
    @State private var enabled = false

    private var saved: VerificationProvider? { model.providers.first { $0.providerId == providerId } }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Toggle(VerificationProvider.displayName(providerId), isOn: $enabled).fontWeight(.medium)
                Spacer()
                Text(saved.map { $0.enabled ? "enabled" : "disabled" } ?? "not set up")
                    .font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                TextField("Path to the CLI", text: $executable).textFieldStyle(.roundedBorder)
                if let found = model.discovered[providerId], found != executable {
                    Button("Use \(URL(fileURLWithPath: found).lastPathComponent) found on PATH") { executable = found }
                        .help(found)
                }
            }
            HStack {
                TextField(modelHint, text: $modelName).textFieldStyle(.roundedBorder).frame(width: 220)
                Stepper("Cost weight \(costWeight)", value: $costWeight, in: 0...1000, step: 5)
                Stepper("\(dailyLimit) per day", value: $dailyLimit, in: 1...500)
                Spacer()
                Button("Save") {
                    model.saveProvider(providerId, executable: executable, model: modelName,
                                       costWeight: costWeight, dailyLimit: dailyLimit, enabled: enabled)
                }
                .disabled(executable.isEmpty)
            }
        }
        .padding(10)
        .background(Color.secondary.opacity(0.05))
        .onAppear(perform: load)
        .onChange(of: model.providers) { _ in load() }
    }

    private func load() {
        guard let saved else {
            costWeight = defaultWeight
            if executable.isEmpty { executable = model.discovered[providerId] ?? "" }
            return
        }
        executable = saved.executable
        modelName = saved.model ?? ""
        costWeight = saved.costWeight
        dailyLimit = saved.dailyLimit
        enabled = saved.enabled
    }
}
