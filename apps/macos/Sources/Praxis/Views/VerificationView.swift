import SwiftUI

/// Requests to check local results with a stronger model. Nothing is sent until the owner
/// reviews the payload, optionally excludes items or redacts phrases, and approves.
struct VerificationView: View {
    @ObservedObject var model: VerificationWorkbenchModel
    @ObservedObject var setup: ModelSetupModel

    private var providers: [VerificationProvider] { setup.providers.filter(\.enabled) }

    private func syncPreferredProvider() {
        model.setPreferredProvider(VerificationProviderDefault.choice(
            checkingProvider: setup.tier("strongest")?.providerId, providers: providers))
    }

    var body: some View {
        MasterDetail(listWidth: (260, 320, 440)) {
            List(selection: Binding<String?>(
                get: { model.selectedId },
                set: { id in if let request = model.requests.first(where: { $0.requestId == id }) { model.select(request) } })) {
                ForEach(model.requests) { request in
                    ItemRow(systemImage: request.awaitingApproval ? "hand.raised" : "checkmark.shield",
                            title: request.reasonLabel,
                            subtitle: "\(RequestStatus.label(request)) · \(Count.label(request.items.count, "item"))",
                            tint: request.awaitingApproval ? .accentColor : .secondary) {
                        if request.awaitingApproval { Circle().fill(Color.accentColor).frame(width: 7, height: 7) }
                    }
                    .tag(request.requestId)
                }
            }
            .overlay {
                if model.requests.isEmpty {
                    EmptyState(systemImage: "checkmark.shield", title: "No verification requests",
                               message: "Praxis suggests one when the local model's evidence reads like an instruction or its entity types disagree.",
                               actionTitle: "Check for New", action: model.plan)
                }
            }
            .safeAreaInset(edge: .bottom, spacing: 0) {
                VStack(alignment: .leading, spacing: Spacing.s) {
                    FeedbackLine(error: model.error, notice: model.notice)
                    if !model.disclosureAudit.isEmpty {
                        DisclosureGroup("Disclosure audit") {
                            VStack(alignment: .leading, spacing: Spacing.xs) {
                                ForEach(model.disclosureAudit.prefix(8)) { event in
                                    Text("\(event.actor) · \(event.operation) · \(event.result) · \(event.reasonCode)")
                                        .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                                }
                            }
                        }
                        .font(.meta)
                    }
                }
                .padding(Spacing.m)
                .background(.bar)
            }
        } detail: {
            if let request = model.selected {
                VerificationDetailView(request: request, model: model, providers: providers)
            } else {
                EmptyState(systemImage: "doc.text.magnifyingglass", title: "Select a request",
                           message: "Review exactly what would be sent, remove items, and redact phrases before approving.")
            }
        }
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Button { model.plan() } label: { Label("Check for New", systemImage: "sparkle.magnifyingglass") }
                    .help("Looks for flagged evidence and type conflicts. Creates requests for review only.")
                Button { model.refresh() } label: { Label("Refresh", systemImage: "arrow.clockwise") }
            }
        }
        .onAppear {
            syncPreferredProvider()
            setup.refresh()
            model.refresh()
        }
        .onChange(of: setup.tiers) { _ in syncPreferredProvider() }
        .onChange(of: setup.providers) { _ in syncPreferredProvider() }
    }
}

enum RequestStatus {
    static func label(_ request: VerificationRequest) -> String {
        switch request.status {
        case "awaiting_approval": return "Waiting for your approval"
        case "approved":
            return request.failureCode.map { "Waiting to retry (\(FailureCode.label($0)))" } ?? "Approved, sending soon"
        case "running": return "Sending"
        case "completed": return "Checked by \(request.providerId.map(VerificationProvider.displayName) ?? "verifier")"
        case "declined": return "Declined"
        default: return "Failed (\(FailureCode.label(request.failureCode ?? "unknown")))"
        }
    }
}

enum FailureCode {
    static func label(_ code: String) -> String {
        switch code {
        case "model_unauthenticated": "sign in to the provider CLI, then re-save it under Models"
        case "model_quota_exhausted": "plan usage limit reached"
        case "model_unavailable": "provider not reachable"
        case "model_timeout": "timed out"
        case "model_invalid_response": "unusable answer"
        case "provider_changed": "provider configuration changed; approve again"
        case "model_missing": "model is not installed"
        case "model_context_exceeded": "reviewed context exceeds the model budget; narrow the request"
        case "model_canceled": "model request canceled; a fresh attempt remains subject to daily limits"
        case "source_changed": "source evidence changed or was revoked; create a new request"
        case "disclosure_denied": "remote disclosure consent, classification or expiry is no longer valid"
        case "disclosure_revoked": "remote disclosure was revoked; create a new request"
        default: code
        }
    }
}

private struct VerificationDetailView: View {
    let request: VerificationRequest
    @ObservedObject var model: VerificationWorkbenchModel
    let providers: [VerificationProvider]
    @State private var phrase = ""
    @State private var pendingSource: String?
    @State private var pendingSourceName = ""
    @State private var confirmRemotePolicy = false

    private var remoteSelected: Bool {
        !model.providerChoice.isEmpty && model.providerChoice != VerificationProvider.ollama
    }

    private var selectedProviderAvailable: Bool {
        VerificationProviderDefault.available(model.providerChoice, providers: providers)
    }

    private var selectedExternalProvider: VerificationProvider? {
        providers.first { $0.providerId == model.providerChoice && $0.providerId != VerificationProvider.ollama }
    }

    private var includedSources: [VerificationDisclosureSource] {
        model.disclosureSources.filter { source in
            source.ordinals.contains { !model.excluded.contains($0) }
        }
    }

    private var remoteReady: Bool {
        !includedSources.isEmpty && includedSources.allSatisfy(\.remoteAllowed)
    }

    private var items: [PayloadItem] { PayloadItem.parse(request.payload) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Spacing.l) {
                VStack(alignment: .leading, spacing: Spacing.xs) {
                    Text(request.reasonLabel).font(.pageTitle)
                    StatusPill(text: RequestStatus.label(request), tone: request.awaitingApproval ? .accent : request.status == "completed" ? .positive : .neutral)
                }
                Panel(title: "Items", systemImage: "list.bullet.rectangle",
                      caption: request.awaitingApproval ? "Uncheck an item to leave it out of the request." : nil) {
                    VStack(alignment: .leading, spacing: Spacing.s) {
                        ForEach(items) { item in itemCard(item) }
                    }
                }
                if request.awaitingApproval {
                    approval
                } else if request.providerId != nil && request.providerId != VerificationProvider.ollama &&
                            (request.status == "approved" || request.status == "running") {
                    Button("Revoke External Disclosure", role: .destructive) { model.revokeDisclosure() }
                }
                if let tokens = request.inputTokens {
                    Text("\(request.providerId.map(VerificationProvider.displayName) ?? "Verifier"), model \(request.modelName ?? "default"): \(tokens) input and \(request.outputTokens ?? 0) output tokens.")
                        .font(.meta).foregroundStyle(.secondary)
                }
                Panel(title: request.awaitingApproval ? "What approval will send" : "What was sent", systemImage: "eye") {
                    Text(model.preview)
                        .font(.system(.caption, design: .monospaced))
                        .textSelection(.enabled)
                        .padding(Spacing.m)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(Surface.well, in: RoundedRectangle(cornerRadius: Radius.s, style: .continuous))
                }
            }
            .padding(Spacing.xl)
            .frame(maxWidth: 900, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func itemCard(_ item: PayloadItem) -> some View {
        let verdict = request.items.first { $0.ordinal == item.ordinal }
        let included = !model.excluded.contains(item.ordinal) && (verdict?.included ?? true)
        return VStack(alignment: .leading, spacing: Spacing.xs) {
            HStack {
                if request.awaitingApproval {
                    Toggle(isOn: Binding(get: { !model.excluded.contains(item.ordinal) },
                                         set: { _ in model.toggle(item.ordinal) })) {
                        Text(shown(item.title)).fontWeight(.medium)
                    }
                } else {
                    Text(item.title).fontWeight(.medium).foregroundStyle(included ? .primary : .secondary)
                }
                Spacer()
                if let verdict, let value = verdict.verdict {
                    Text(value + (verdict.correctedType.map { " · type: \($0)" } ?? ""))
                        .font(.caption.weight(.semibold))
                        .foregroundColor(value == "supported" ? .green : value == "unsupported" ? .orange : .secondary)
                } else if !included {
                    Text("not sent").font(.caption).foregroundStyle(.secondary)
                }
            }
            ForEach(Array(item.quotes.enumerated()), id: \.offset) { _, quote in
                Text("“\(shown(quote.quote))” — \(quote.source)").font(.callout).foregroundStyle(.secondary).textSelection(.enabled)
            }
            if let note = verdict?.note {
                Text(note).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            }
        }
        .padding(Spacing.m)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Surface.well, in: RoundedRectangle(cornerRadius: Radius.m, style: .continuous))
    }

    private var approval: some View {
        Panel(title: "Before approving", systemImage: "hand.raised",
              caption: "Unchecked items are removed. A name can still appear in another item's quote; redact it to remove it everywhere.") {
            VStack(alignment: .leading, spacing: Spacing.m) {
                HStack {
                    TextField("Phrase to redact", text: $phrase).textFieldStyle(.roundedBorder)
                        .onSubmit(addPhrase)
                    Button("Redact", action: addPhrase)
                        .disabled(phrase.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                if !model.redactions.isEmpty {
                    FlowLayout(spacing: Spacing.xs) {
                        ForEach(model.redactions, id: \.self) { redaction in
                            Chip(text: redaction, systemImage: "eye.slash") { model.removeRedaction(redaction) }
                        }
                    }
                }
                if remoteSelected {
                    Text("Sources permitted for external verification")
                        .font(.subheadline.weight(.semibold))
                    ForEach(includedSources) { source in
                        HStack {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(source.name.isEmpty ? source.sourceId : source.name)
                                Text(source.remoteAllowed ? "Remote verification permitted" : "Local-only source")
                                    .font(.caption).foregroundColor(source.remoteAllowed ? .secondary : .orange)
                            }
                            Spacer()
                            if source.remoteAllowed {
                                Button("Return to local-only") {
                                    model.setRemoteVerification(source.sourceId, allowed: false)
                                }
                                .controlSize(.small)
                            } else {
                                Button("Review classification") {
                                    pendingSource = source.sourceId
                                    pendingSourceName = source.name.isEmpty ? source.sourceId : source.name
                                    confirmRemotePolicy = true
                                }
                                .controlSize(.small)
                            }
                        }
                    }
                    if let provider = selectedExternalProvider {
                        Text("Destination: \(VerificationProvider.displayName(provider.providerId))")
                            .font(.caption.weight(.semibold))
                        Text("CLI: \(provider.executable ?? "not configured") · Model: \(provider.model ?? "provider default") · Configuration revision: \(provider.revision)")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .textSelection(.enabled)
                    }
                    Toggle("I approve sending these redacted excerpts to \(VerificationProvider.displayName(model.providerChoice))",
                           isOn: $model.remoteConsent)
                        .font(.caption)
                    Text("Source classification and this request's consent are separate. Consent expires after 15 minutes, and you can revoke it while approved or running. Previously transmitted content cannot be recalled.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                HStack {
                    Picker("Send to", selection: Binding(
                        get: { model.providerChoice },
                        set: { model.chooseProvider($0) })) {
                        Text("Local Ollama (if enabled)").tag("")
                        ForEach(providers.filter { $0.providerId != VerificationProvider.ollama }) { provider in
                            Text(VerificationProvider.displayName(provider.providerId)).tag(provider.providerId)
                        }
                    }
                    .frame(maxWidth: 320)
                    Spacer()
                    Button("Decline") { model.decline() }
                    Button("Approve and Send") { model.approve() }
                        .buttonStyle(.borderedProminent)
                        .disabled(!selectedProviderAvailable || model.excluded.count >= items.count ||
                            (remoteSelected && (!model.remoteConsent || !remoteReady)))
                }
                if model.providerChoice == "" {
                    Text("Local Ollama is selected. The Checking model can be changed under Settings → Models; selecting a remote provider never sends data without approval.")
                        .font(.caption).foregroundStyle(.secondary)
                } else if model.providerChoice != VerificationProvider.ollama {
                    Text("The configured Checking provider is suggested when available. External disclosure still requires each included source's permission and your explicit approval of the redacted payload.")
                        .font(.caption).foregroundColor(.orange)
                }
                if !selectedProviderAvailable {
                    Text("The selected verifier is not enabled. Choose an available provider under Send to, or connect one in Models.")
                        .font(.caption).foregroundColor(.orange)
                }
            }
        }
        .alert("Allow remote verification for \(pendingSourceName)?", isPresented: $confirmRemotePolicy) {
            Button("Allow remote verification", role: .destructive) {
                if let pendingSource, includedSources.contains(where: { $0.sourceId == pendingSource }) {
                    model.setRemoteVerification(pendingSource, allowed: true)
                }
                pendingSource = nil
                pendingSourceName = ""
            }
            Button("Cancel", role: .cancel) {
                pendingSource = nil
                pendingSourceName = ""
            }
        } message: {
            Text("This changes the source from local-only to remote-verification eligible. Separately approve the exact provider and redacted payload before anything is sent. Do not allow sensitive local-only sources.")
        }
    }

    /// Quotes as they will be sent: redactions apply while the request awaits approval.
    private func shown(_ text: String) -> String {
        request.awaitingApproval ? PayloadPreview.redact(text, model.redactions) : text
    }

    private func addPhrase() {
        model.addRedaction(phrase)
        phrase = ""
    }
}
