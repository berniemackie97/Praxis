import SwiftUI

/// Requests to check local results with a stronger model. Nothing is sent until the owner
/// reviews the payload, optionally excludes items or redacts phrases, and approves.
struct VerificationView: View {
    @ObservedObject var model: VerificationWorkbenchModel
    let providers: [VerificationProvider]

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("Requests").font(.headline)
                    Spacer()
                    Button("Check for new") { model.plan() }
                        .help("Looks for flagged evidence and type conflicts. Creates requests for review only.")
                    Button("Refresh") { model.refresh() }
                }
                if model.requests.isEmpty {
                    Text("No verification requests. Praxis suggests them when the local model's evidence reads like an instruction or its entity types disagree.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                List(model.requests) { request in
                    Button { model.select(request) } label: {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(request.reasonLabel).fontWeight(.medium)
                            Text("\(RequestStatus.label(request)) · \(request.items.count) item(s)")
                                .font(.caption)
                                .foregroundColor(request.awaitingApproval ? .accentColor : .secondary)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .listRowBackground(model.selectedId == request.requestId ? Color.accentColor.opacity(0.15) : Color.clear)
                }
                FeedbackLine(error: model.error, notice: model.notice)
            }
            .frame(width: 320)
            Divider()
            if let request = model.selected {
                VerificationDetailView(request: request, model: model, providers: providers.filter(\.enabled))
            } else {
                Text("Select a request to review exactly what would be sent.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }
        }
        .onAppear { model.refresh() }
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
        default: code
        }
    }
}

private struct VerificationDetailView: View {
    let request: VerificationRequest
    @ObservedObject var model: VerificationWorkbenchModel
    let providers: [VerificationProvider]
    @State private var phrase = ""

    private var items: [PayloadItem] { PayloadItem.parse(request.payload) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                Text(request.reasonLabel).font(.title3.weight(.semibold))
                Text(RequestStatus.label(request)).foregroundStyle(.secondary)
                ForEach(items) { item in itemCard(item) }
                if request.awaitingApproval {
                    approval
                } else if let tokens = request.inputTokens {
                    Text("\(request.providerId.map(VerificationProvider.displayName) ?? "Verifier"), model \(request.modelName ?? "default"): \(tokens) input and \(request.outputTokens ?? 0) output tokens, counted against the selected provider's daily request allowance.")

                        .font(.caption).foregroundStyle(.secondary)
                }
                Text(request.awaitingApproval ? "What approval will send" : "What was sent").font(.headline)
                Text(model.preview)
                    .font(.system(.caption, design: .monospaced))
                    .textSelection(.enabled)
                    .padding(8)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(Color.secondary.opacity(0.08))
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func itemCard(_ item: PayloadItem) -> some View {
        let verdict = request.items.first { $0.ordinal == item.ordinal }
        let included = !model.excluded.contains(item.ordinal) && (verdict?.included ?? true)
        return VStack(alignment: .leading, spacing: 4) {
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
                Text("“\(shown(quote.quote))” — \(quote.source)").font(.caption).textSelection(.enabled)
            }
            if let note = verdict?.note {
                Text(note).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            }
        }
        .padding(8)
        .background(Color.secondary.opacity(0.05))
    }

    private var approval: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Before approving").font(.headline)
            Text("Unchecked items are removed. A name can still appear in another item's quote; redact it to remove it everywhere.")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                TextField("Phrase to redact", text: $phrase).textFieldStyle(.roundedBorder)
                    .onSubmit(addPhrase)
                Button("Redact", action: addPhrase)
                    .disabled(phrase.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            if !model.redactions.isEmpty {
                HStack {
                    ForEach(model.redactions, id: \.self) { redaction in
                        Button("\(redaction) ✕") { model.removeRedaction(redaction) }
                            .buttonStyle(.bordered).controlSize(.small)
                    }
                }
            }
            HStack {
                Picker("Send to", selection: $model.providerChoice) {
                    Text("Local Ollama (if enabled)").tag("")
                    ForEach(providers) { provider in
                        Text(VerificationProvider.displayName(provider.providerId)).tag(provider.providerId)
                    }
                }
                .frame(maxWidth: 320)
                Spacer()
                Button("Decline") { model.decline() }
                Button("Approve and send") { model.approve() }
                    .buttonStyle(.borderedProminent)
                    .disabled(providers.isEmpty || model.excluded.count >= items.count)
            }
            if model.providerChoice == "" {
                Text("With no provider selected, only an enabled local Ollama verifier may run. Remote CLIs are never chosen automatically.")
                    .font(.caption).foregroundStyle(.secondary)
            } else if model.providerChoice != VerificationProvider.ollama {
                Text("External disclosure: approval sends exactly the reviewed and redacted payload through the selected signed-in CLI. Subscription limits apply.")
                    .font(.caption).foregroundColor(.orange)
            }
            if providers.isEmpty {
                Text("Enable a local Ollama verifier or an explicitly selected CLI under Models first.")
                    .font(.caption).foregroundColor(.orange)
            }
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
