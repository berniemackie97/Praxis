import Combine
import SwiftUI

/// Owner-selected local and explicit subscription models for knowledge verification.
struct IntelligenceSettingsView: View {
    @ObservedObject var model: IntelligenceSettingsModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                LocalModelSettings(model: model)
                Divider()
                LocalEmbeddingSettings(model: model)
                Divider()
                VStack(alignment: .leading, spacing: 6) {
                    Text("Stronger-model verification").font(.headline)
                    Text("Local Ollama runs only on this Mac. Codex and Claude CLIs can send the reviewed payload online only when explicitly selected during approval. No API keys are used. Daily limits count every attempted invocation, including retries.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                ProviderSettings(model: model, providerId: VerificationProvider.ollama, defaultWeight: 0,
                                 modelHint: "Installed local chat model")
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
        .onAppear { load(model.profile) }
        .onReceive(model.$profile.removeDuplicates().dropFirst()) { profile in
            load(profile)
        }
    }

    private func load(_ profile: ModelProfile?) {
        guard let profile else { return }
        endpoint = profile.endpoint.hasSuffix("/") ? String(profile.endpoint.dropLast()) : profile.endpoint
        modelName = profile.model
        contextTokens = profile.contextTokens
        enabled = profile.enabled
    }
}

private struct LocalEmbeddingSettings: View {
    @ObservedObject var model: IntelligenceSettingsModel
    @State private var endpoint = "http://127.0.0.1:11434"
    @State private var modelName = ""
    @State private var enabled = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Local semantic retrieval").font(.headline)
            Text("Optional, offline embeddings via a model installed in your own Ollama runtime. No remote API or automatic model download. Select an embedding-capable model, then update the index in Search.")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                TextField("Ollama address", text: $endpoint).textFieldStyle(.roundedBorder)
                    .frame(width: 240)
                Button("List installed models") { model.listModels(endpoint: endpoint) }
            }
            HStack {
                Picker("Embedding model", selection: $modelName) {
                    if modelName.isEmpty { Text("Choose a model").tag("") }
                    if !modelName.isEmpty && !model.installedModels.contains(where: { $0.name == modelName }) {
                        Text(modelName).tag(modelName)
                    }
                    ForEach(model.installedModels) { installed in
                        Text(installed.name).tag(installed.name)
                    }
                }
                .frame(maxWidth: 420)
                Toggle("Enable hybrid search", isOn: $enabled)
                Button("Save") {
                    model.saveEmbeddingProfile(endpoint: endpoint, model: modelName, enabled: enabled)
                }
                .disabled(modelName.isEmpty)
            }
        }
        .onAppear { load(model.embeddingProfile) }
        .onReceive(model.$embeddingProfile.removeDuplicates().dropFirst()) { profile in
            load(profile)
        }
    }

    private func load(_ profile: ModelProfile?) {
        guard let profile else { return }
        endpoint = profile.endpoint.hasSuffix("/") ? String(profile.endpoint.dropLast()) : profile.endpoint
        modelName = profile.model
        enabled = profile.enabled
    }
}

private struct ProviderSettings: View {
    @ObservedObject var model: IntelligenceSettingsModel
    let providerId: String
    let defaultWeight: Int
    let modelHint: String
    @State private var executable = ""
    @State private var endpoint = "http://127.0.0.1:11434"
    @State private var modelName = ""
    @State private var costWeight = 10
    @State private var dailyLimit = 20
    @State private var enabled = false

    private var saved: VerificationProvider? {
        model.providers.first { $0.providerId == providerId }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Toggle(VerificationProvider.displayName(providerId), isOn: $enabled).fontWeight(.medium)
                Spacer()
                Text(saved.map { $0.enabled ? "enabled" : "disabled" } ?? "not set up")
                    .font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                if providerId == VerificationProvider.ollama {
                    TextField("Loopback Ollama endpoint", text: $endpoint).textFieldStyle(.roundedBorder)
                    if let profile = model.profile {
                        Button("Use enrichment model") {
                            endpoint = profile.endpoint
                            modelName = profile.model
                        }
                    }
                } else {
                    TextField("Path to the CLI", text: $executable).textFieldStyle(.roundedBorder)
                    if let found = model.discovered[providerId], found != executable {
                        Button("Use \(URL(fileURLWithPath: found).lastPathComponent) found on PATH") { executable = found }
                            .help(found)
                    }
                }
            }
            HStack {
                TextField(modelHint, text: $modelName).textFieldStyle(.roundedBorder).frame(width: 220)
                Stepper("Cost weight \(costWeight)", value: $costWeight, in: 0...1000, step: 5)
                Stepper("\(dailyLimit) per day", value: $dailyLimit, in: 1...500)
                Spacer()
                Button("Save") {
                    model.saveProvider(providerId, executable: executable, endpoint: endpoint,
                                       model: modelName, costWeight: costWeight,
                                       dailyLimit: dailyLimit, enabled: enabled)
                }
                .disabled(providerId == VerificationProvider.ollama
                    ? (endpoint.isEmpty || modelName.isEmpty) : executable.isEmpty)
            }
        }
        .padding(10)
        .background(Color.secondary.opacity(0.05))
        .onAppear { load(model.providers) }
        .onReceive(model.$providers.removeDuplicates().dropFirst()) { providers in
            load(providers)
        }
    }

    private func load(_ providers: [VerificationProvider]) {
        guard let saved = providers.first(where: { $0.providerId == providerId }) else {
            costWeight = defaultWeight
            if executable.isEmpty { executable = model.discovered[providerId] ?? "" }
            return
        }
        executable = saved.executable ?? ""
        endpoint = saved.endpoint ?? endpoint
        modelName = saved.model ?? ""
        costWeight = saved.costWeight
        dailyLimit = saved.dailyLimit
        enabled = saved.enabled
    }
}
