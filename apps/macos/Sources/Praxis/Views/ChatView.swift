import SwiftUI

/// Conversations with the Praxis agent: answers grounded in the owner's sources through tools,
/// with numbered citations, the answer check's verdict, and anything disclosure policy withheld.
struct ChatView: View {
    @ObservedObject var model: ChatWorkbenchModel
    let openPrivacy: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            ConversationList(model: model)
                .frame(width: 260)
            Divider()
            VStack(alignment: .leading, spacing: 10) {
                if model.backends.isEmpty {
                    NoBackendNotice(openPrivacy: openPrivacy)
                }
                Transcript(model: model)
                if model.running {
                    HStack(spacing: 8) {
                        ProgressView().controlSize(.small)
                        Text(ChatActivity.progress(model.events.last)).font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Button("Stop") { model.cancel() }
                    }
                } else if let reply = model.latestReply {
                    ReplyDetails(reply: reply, openPrivacy: openPrivacy)
                }
                Composer(model: model)
                FeedbackLine(error: model.error, notice: model.notice)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .onAppear { model.refresh() }
    }
}

private struct ConversationList: View {
    @ObservedObject var model: ChatWorkbenchModel

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Conversations").font(.headline)
                Spacer()
                Button("New") { model.newConversation() }
            }
            Picker("Model", selection: $model.backendChoice) {
                ForEach(model.backends.filter(\.enabled)) { backend in
                    Text("\(backend.backendId) · \(backend.kindLabel)").tag(backend.backendId)
                }
            }
            .disabled(model.selectedId != nil)
            .help("Used for new conversations. Remote models see only what your disclosure policy grants for chat.")
            List(model.conversations) { conversation in
                Button { model.select(conversation) } label: {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(conversation.title).lineLimit(2)
                        Text(conversation.backendId).font(.caption).foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .listRowBackground(model.selectedId == conversation.conversationId ? Color.accentColor.opacity(0.15) : Color.clear)
            }
        }
    }
}

private struct Transcript: View {
    @ObservedObject var model: ChatWorkbenchModel

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 10) {
                    if model.transcript.isEmpty {
                        Text("Ask about anything you have captured — documents, notes, statements. Praxis answers from your sources and cites them.")
                            .foregroundStyle(.secondary)
                    }
                    ForEach(model.transcript) { line in
                        ChatBubble(line: line).id(line.id)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, 4)
            }
            .onChange(of: model.transcript.count) { _ in
                if let last = model.transcript.last { proxy.scrollTo(last.id, anchor: .bottom) }
            }
        }
    }
}

private struct ChatBubble: View {
    let line: ChatLine

    var body: some View {
        switch line.kind {
        case .owner:
            HStack {
                Spacer(minLength: 60)
                Text(line.text).textSelection(.enabled).padding(10)
                    .background(Color.accentColor.opacity(0.15), in: RoundedRectangle(cornerRadius: 10))
            }
        case .agent:
            Text(line.text).textSelection(.enabled).padding(10)
                .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
                .padding(.trailing, 60)
        case .activity:
            Label(line.text, systemImage: "wrench.and.screwdriver").font(.caption).foregroundStyle(.secondary).lineLimit(3)
        case .summary:
            Text(line.text).font(.caption).italic().foregroundStyle(.secondary)
        }
    }
}

private struct ReplyDetails: View {
    let reply: AgentReply
    let openPrivacy: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if !reply.check.clean {
                Label(AnswerCheckText.describe(reply.check), systemImage: "exclamationmark.triangle")
                    .font(.caption).foregroundColor(.orange)
            }
            if reply.withheldResults > 0 {
                HStack {
                    Label("\(reply.withheldResults) item(s) were withheld from this model by your disclosure policy.", systemImage: "lock")
                        .font(.caption).foregroundStyle(.secondary)
                    Button("Review policy", action: openPrivacy).font(.caption)
                }
            }
            if !reply.citations.isEmpty {
                DisclosureGroup("Sources (\(reply.citations.count))") {
                    ForEach(Array(reply.citations.enumerated()), id: \.offset) { index, citation in
                        Text("[\(index + 1)] \(citation.sourceName) · \(citation.locator)")
                            .font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                    }
                }
                .font(.caption)
            }
        }
    }
}

private struct Composer: View {
    @ObservedObject var model: ChatWorkbenchModel

    var body: some View {
        HStack(alignment: .bottom, spacing: 8) {
            TextEditor(text: $model.draft)
                .font(.body)
                .frame(minHeight: 44, maxHeight: 120)
                .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.secondary.opacity(0.3)))
            VStack(alignment: .trailing, spacing: 4) {
                if model.draft.count > ChatWorkbenchModel.maximumMessageCharacters {
                    Text("Too long").font(.caption).foregroundColor(.orange)
                }
                Button("Send") { model.send() }
                    .keyboardShortcut(.return, modifiers: .command)
                    .disabled(!model.canSend)
            }
        }
    }
}

private struct NoBackendNotice: View {
    let openPrivacy: () -> Void

    var body: some View {
        HStack {
            Text("Add a chat model first — a local model, an API, or Claude Code / Codex.").foregroundStyle(.secondary)
            Button("Set up models", action: openPrivacy)
        }
    }
}

/// How the answer check's findings are shown.
enum AnswerCheckText {
    static func describe(_ check: AnswerCheck) -> String {
        var parts: [String] = []
        if !check.unsupportedFigures.isEmpty {
            parts.append("Figures not found in any tool result: \(check.unsupportedFigures.joined(separator: ", ")).")
        }
        if check.missingCitations { parts.append("Some claims are not cited.") }
        return (check.repaired ? "Still flagged after one correction: " : "Answer check: ") + parts.joined(separator: " ")
    }
}
