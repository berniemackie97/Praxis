import Combine
import SwiftUI

/// The local models Praxis runs on this Mac and the providers that can fill intake tiers or verify.
struct IntelligenceSettingsView: View {
    @ObservedObject var model: IntelligenceSettingsModel

    var body: some View {
        Form {
            if let message = model.error ?? model.notice {
                Banner(text: message, tone: model.error == nil ? .accent : .warning,
                       systemImage: model.error == nil ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
            }
            LocalModelSettings(model: model)
            LocalEmbeddingSettings(model: model)
            ProviderSettings(model: model, providerId: VerificationProvider.ollama, defaultWeight: 0,
                             modelHint: "Installed local chat model")
            ProviderSettings(model: model, providerId: VerificationProvider.codex, defaultWeight: 10,
                             modelHint: "Leave empty for the plan default")
            ProviderSettings(model: model, providerId: VerificationProvider.claudeCode, defaultWeight: 50,
                             modelHint: "sonnet, haiku or opus")
        }
        .formStyle(.grouped)
        .onAppear { model.refresh() }
    }
}

/// An Ollama address with a button that lists its installed models.
private struct OllamaAddress: View {
    @ObservedObject var model: IntelligenceSettingsModel
    @Binding var endpoint: String

    var body: some View {
        LabeledContent("Ollama address") {
            HStack {
                TextField("Ollama address", text: $endpoint).labelsHidden()
                Button("Find Models") { model.listModels(endpoint: endpoint) }
            }
        }
    }
}

private struct InstalledModelPicker: View {
    let title: String
    @ObservedObject var model: IntelligenceSettingsModel
    @Binding var selection: String
    var showSizes = true

    var body: some View {
        Picker(title, selection: $selection) {
            if selection.isEmpty { Text("Choose a model").tag("") }
            if !selection.isEmpty && !model.installedModels.contains(where: { $0.name == selection }) {
                Text(selection).tag(selection)
            }
            ForEach(model.installedModels) { installed in
                Text(showSizes ? "\(installed.name)  (\(ByteCountFormatter.string(fromByteCount: installed.sizeBytes, countStyle: .file)))" : installed.name)
                    .tag(installed.name)
            }
        }
    }
}

private struct LocalModelSettings: View {
    @ObservedObject var model: IntelligenceSettingsModel
    @State private var endpoint = "http://127.0.0.1:11434"
    @State private var modelName = ""
    @State private var contextTokens = 8_192
    @State private var enabled = true

    var body: some View {
        Section {
            OllamaAddress(model: model, endpoint: $endpoint)
            InstalledModelPicker(title: "Model", model: model, selection: $modelName)
            Stepper("Context: \(contextTokens.formatted()) tokens", value: $contextTokens, in: 2_048...32_768, step: 2_048)
            Toggle("Enrich new and existing text", isOn: $enabled)
            HStack {
                Spacer()
                Button("Save") {
                    model.saveProfile(endpoint: endpoint, model: modelName, contextTokens: contextTokens, enabled: enabled)
                }
                .disabled(modelName.isEmpty)
            }
        } header: {
            Text("Local enrichment model")
        } footer: {
            Text("Runs on this Mac through Ollama. It suggests topics, entities and relations for captured text; nothing leaves the Mac.")
                .font(.meta).foregroundStyle(.secondary)
        }
        .onAppear { load(model.profile) }
        .onReceive(model.$profile.removeDuplicates().dropFirst()) { load($0) }
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
        Section {
            OllamaAddress(model: model, endpoint: $endpoint)
            InstalledModelPicker(title: "Embedding model", model: model, selection: $modelName, showSizes: false)
            Toggle("Enable hybrid search", isOn: $enabled)
            HStack {
                Spacer()
                Button("Save") { model.saveEmbeddingProfile(endpoint: endpoint, model: modelName, enabled: enabled) }
                    .disabled(modelName.isEmpty)
            }
        } header: {
            Text("Semantic retrieval")
        } footer: {
            Text("Optional offline embeddings from a model in your own Ollama. No remote API or automatic download. Update the index from Search.")
                .font(.meta).foregroundStyle(.secondary)
        }
        .onAppear { load(model.embeddingProfile) }
        .onReceive(model.$embeddingProfile.removeDuplicates().dropFirst()) { load($0) }
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
        Section {
            Toggle("Enabled", isOn: $enabled)
            if providerId == VerificationProvider.ollama {
                LabeledContent("Endpoint") {
                    HStack {
                        TextField("Loopback Ollama endpoint", text: $endpoint).labelsHidden()
                        if let profile = model.profile {
                            Button("Use Enrichment Model") {
                                endpoint = profile.endpoint
                                modelName = profile.model
                            }
                        }
                    }
                }
            } else {
                LabeledContent("Command-line tool") {
                    HStack {
                        TextField("Path to the CLI", text: $executable).labelsHidden()
                        if let found = model.discovered[providerId], found != executable {
                            Button("Use Found") { executable = found }.help(found)
                        }
                    }
                }
            }
            TextField("Model", text: $modelName, prompt: Text(modelHint))
            Stepper("Cost weight: \(costWeight)", value: $costWeight, in: 0...1000, step: 5)
            Stepper("Daily limit: \(dailyLimit) attempts", value: $dailyLimit, in: 1...500)
            HStack {
                if let allowance = model.allowances.first(where: { $0.provider.providerId == providerId }) {
                    Text("\(allowance.startedToday) of \(allowance.provider.dailyLimit) used today").font(.meta).foregroundStyle(.secondary)
                }
                Spacer()
                Button("Save") {
                    model.saveProvider(providerId, executable: executable, endpoint: endpoint,
                                       model: modelName, costWeight: costWeight,
                                       dailyLimit: dailyLimit, enabled: enabled)
                }
                .disabled(providerId == VerificationProvider.ollama
                    ? (endpoint.isEmpty || modelName.isEmpty) : executable.isEmpty)
            }
        } header: {
            HStack {
                Text(VerificationProvider.displayName(providerId))
                Spacer()
                StatusPill(text: saved.map { $0.enabled ? "Enabled" : "Disabled" } ?? "Not set up",
                           tone: saved?.enabled == true ? .positive : .neutral)
            }
        } footer: {
            if providerId == VerificationProvider.ollama {
                Text("Runs only on this Mac.").font(.meta).foregroundStyle(.secondary)
            } else {
                Text("Uses your plan, never API keys. Sends content online only for items you approve or categories you grant. Daily limits count every attempt, including retries.")
                    .font(.meta).foregroundStyle(.secondary)
            }
        }
        .onAppear { load(model.providers) }
        .onReceive(model.$providers.removeDuplicates().dropFirst()) { load($0) }
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
