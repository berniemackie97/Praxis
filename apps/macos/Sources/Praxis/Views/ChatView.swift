import SwiftUI

/// Conversations with the Praxis agent: answers grounded in the owner's sources through tools,
/// with numbered citations, the answer check's verdict, and anything disclosure policy withheld.
struct ChatView: View {
    @ObservedObject var model: ChatWorkbenchModel
    let openAgentSettings: () -> Void

    var body: some View {
        Group {
            if model.backends.isEmpty && model.conversations.isEmpty {
                EmptyState(systemImage: "cpu",
                           title: "Connect a model to start",
                           message: "Praxis can chat through a local model, an API key, or your Claude Code or Codex plan. Remote models only see what your disclosure policy grants.",
                           actionTitle: "Set Up Models…", action: openAgentSettings)
            } else {
                conversations
            }
        }
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                if model.selectedId == nil && !model.backends.isEmpty {
                    Picker("Model", selection: $model.backendChoice) {
                        ForEach(model.backends.filter(\.enabled)) { backend in
                            Text(ChatBackendLabel.of(backend)).tag(backend.backendId)
                        }
                    }
                    .help("Model for the new conversation. Remote models see only what your disclosure policy grants for chat.")
                } else if let conversation = model.selected {
                    StatusPill(text: conversation.backendId, tone: .neutral, systemImage: "cpu")
                        .help("Each conversation stays on the model it started with.")
                }
                Button { model.newConversation() } label: { Label("New Conversation", systemImage: "square.and.pencil") }
                    .help("New conversation (⌘N)")
            }
        }
        .onAppear { model.refresh() }
    }

    private var conversations: some View {
        MasterDetail(listWidth: (200, 240, 340)) {
            ConversationList(model: model)
        } detail: {
            VStack(spacing: 0) {
                if model.backends.isEmpty {
                    EmptyState(systemImage: "cpu",
                               title: "Connect a model to start",
                               message: "Praxis can chat through a local model, an API key, or your Claude Code or Codex plan. Remote models only see what your disclosure policy grants.",
                               actionTitle: "Set Up Models…", action: openAgentSettings)
                } else {
                    Transcript(model: model, openAgentSettings: openAgentSettings)
                    Composer(model: model)
                }
            }
            .background(Color(nsColor: .textBackgroundColor))
        }
    }
}

private struct ConversationList: View {
    @ObservedObject var model: ChatWorkbenchModel
    @State private var renaming: AgentConversation?
    @State private var renameTitle = ""
    @State private var deleting: AgentConversation?

    var body: some View {
        List(selection: Binding<String?>(
            get: { model.selectedId },
            set: { id in
                if let conversation = model.conversations.first(where: { $0.conversationId == id }) {
                    model.select(conversation)
                }
            })) {
            ForEach(model.conversations) { conversation in
                VStack(alignment: .leading, spacing: Spacing.xxs) {
                    Text(conversation.title).font(.rowTitle).lineLimit(2)
                    Text("\(conversation.backendId) · \(DateText.relative(conversation.updatedUtc))")
                        .font(.meta).foregroundStyle(.secondary).lineLimit(1)
                }
                .padding(.vertical, Spacing.xxs)
                .tag(conversation.conversationId)
                .contextMenu {
                    Button("Rename…", systemImage: "pencil") {
                        renaming = conversation
                        renameTitle = conversation.title
                    }
                    Button("Delete…", systemImage: "trash", role: .destructive) {
                        deleting = conversation
                    }
                }
            }
        }
        .alert("Rename Conversation", isPresented: Binding(
            get: { renaming != nil },
            set: { if !$0 { renaming = nil } })) {
            TextField("Name", text: $renameTitle)
            Button("Save") {
                if let renaming { model.rename(renaming, to: renameTitle) }
                renaming = nil
            }
            Button("Cancel", role: .cancel) { renaming = nil }
        }
        .confirmationDialog("Delete this conversation?", isPresented: Binding(
            get: { deleting != nil },
            set: { if !$0 { deleting = nil } }), titleVisibility: .visible) {
            Button("Delete Conversation", role: .destructive) {
                if let deleting { model.delete(deleting) }
                deleting = nil
            }
        } message: {
            Text("The conversation and its tool-call history will be deleted. Separately saved knowledge, memories and artifacts stay available.")
        }
        .overlay {
            if model.conversations.isEmpty {
                Text("No conversations yet").font(.callout).foregroundStyle(.secondary)
            }
        }
    }
}

private struct Transcript: View {
    @ObservedObject var model: ChatWorkbenchModel
    let openAgentSettings: () -> Void

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: Spacing.l) {
                    if model.transcript.isEmpty && !model.running {
                        Welcome(model: model)
                    }
                    ForEach(model.transcript) { line in
                        ChatBubble(line: line).id(line.id)
                    }
                    if model.running {
                        Thinking(model: model).id("thinking")
                    } else if let reply = model.latestReply, !model.transcript.isEmpty {
                        ReplyDetails(reply: reply, model: model, openAgentSettings: openAgentSettings)
                    }
                    FeedbackLine(error: model.error, notice: model.notice)
                }
                .frame(maxWidth: 760, alignment: .leading)
                .padding(.horizontal, Spacing.xl)
                .padding(.vertical, Spacing.xl)
                .frame(maxWidth: .infinity)
            }
            .onChange(of: model.transcript.count) { _ in
                withAnimation(.easeOut(duration: 0.2)) {
                    if let last = model.transcript.last { proxy.scrollTo(last.id, anchor: .bottom) }
                }
            }
            .onChange(of: model.running) { running in
                if running { withAnimation { proxy.scrollTo("thinking", anchor: .bottom) } }
            }
        }
    }
}

private struct Welcome: View {
    @ObservedObject var model: ChatWorkbenchModel

    private static let prompts = [
        ("calendar", "What's on my plate this week?"),
        ("creditcard", "How much did I spend on groceries last month?"),
        ("doc.text.magnifyingglass", "Summarize what I captured recently"),
        ("person.2", "Who comes up most across my notes?")
    ]

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.l) {
            Image(systemName: "sparkles")
                .font(.system(size: 28, weight: .light))
                .foregroundStyle(Color.accentColor)
            VStack(alignment: .leading, spacing: Spacing.xs) {
                Text("Ask anything about what you've captured").font(.title2.weight(.semibold))
                Text("Praxis answers from your documents, notes and statements, cites every source, and checks figures against the data before you see them.")
                    .font(.callout).foregroundStyle(.secondary)
            }
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 240), spacing: Spacing.m)], spacing: Spacing.m) {
                ForEach(Self.prompts, id: \.1) { symbol, prompt in
                    Button {
                        model.draft = prompt
                        if model.canSend { model.send() }
                    } label: {
                        HStack(spacing: Spacing.s) {
                            Image(systemName: symbol).foregroundStyle(Color.accentColor).frame(width: 18)
                            Text(prompt).multilineTextAlignment(.leading)
                            Spacer(minLength: 0)
                        }
                        .padding(Spacing.m)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(Surface.well, in: RoundedRectangle(cornerRadius: Radius.m, style: .continuous))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .padding(.top, Spacing.xxl)
    }
}

private struct ChatBubble: View {
    let line: ChatLine

    var body: some View {
        switch line.kind {
        case .owner:
            HStack {
                Spacer(minLength: 80)
                Text(line.text)
                    .textSelection(.enabled)
                    .padding(.horizontal, Spacing.m)
                    .padding(.vertical, Spacing.s + 2)
                    .background(Color.accentColor.opacity(0.14), in: RoundedRectangle(cornerRadius: Radius.l, style: .continuous))
            }
        case .agent:
            HStack(alignment: .top, spacing: Spacing.m) {
                Image(systemName: "sparkles")
                    .foregroundStyle(Color.accentColor)
                    .frame(width: 22, height: 22)
                    .background(Color.accentColor.opacity(0.12), in: Circle())
                Text(Markdown.inline(line.text))
                    .textSelection(.enabled)
                    .lineSpacing(3)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        case .activity:
            Label(line.text, systemImage: "wrench.and.screwdriver")
                .font(.meta).foregroundStyle(.secondary).lineLimit(2)
                .padding(.leading, 34)
        case .summary:
            Text(line.text).font(.meta).italic().foregroundStyle(.secondary)
                .padding(.leading, 34)
        }
    }
}

private struct Thinking: View {
    @ObservedObject var model: ChatWorkbenchModel

    var body: some View {
        HStack(spacing: Spacing.s) {
            ProgressView().controlSize(.small)
            Text(ChatActivity.progress(model.events.last)).font(.callout).foregroundStyle(.secondary)
            Spacer()
            Button("Stop", role: .cancel) { model.cancel() }
                .controlSize(.small)
                .keyboardShortcut(".", modifiers: .command)
        }
        .padding(.leading, 34)
    }
}

private struct ReplyDetails: View {
    let reply: AgentReply
    @ObservedObject var model: ChatWorkbenchModel
    let openAgentSettings: () -> Void

    /// Cited clips, played inline; the first few are shown as players, all stay listed as chips.
    private var clips: [(number: Int, citation: AgentCitation, range: MediaTimeRange)] {
        Array(reply.citations.enumerated().compactMap { index, citation in
            MediaTimeRange(locator: citation.locator).map { (index + 1, citation, $0) }
        }.prefix(3))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            ForEach(clips, id: \.citation.id) { clip in
                MediaClipCard(number: clip.number, citation: clip.citation, range: clip.range, model: model)
            }
            if !reply.citations.isEmpty {
                FlowLayout(spacing: Spacing.xs) {
                    ForEach(Array(reply.citations.enumerated()), id: \.offset) { index, citation in
                        Chip(text: chipText(index, citation), systemImage: SourceSymbol.of(citation.sourceName))
                            .help("[\(index + 1)] \(citation.sourceName) · \(citation.locator)")
                    }
                }
            }
            if !reply.check.clean {
                Banner(text: AnswerCheckText.describe(reply.check), tone: .warning, systemImage: "exclamationmark.triangle.fill")
            }
            if reply.withheldResults > 0 {
                HStack(spacing: Spacing.s) {
                    Image(systemName: "lock.fill").foregroundStyle(.secondary)
                    Text("\(Count.label(reply.withheldResults, "item")) withheld from this model by your disclosure policy.")
                        .font(.meta).foregroundStyle(.secondary)
                    Button("Review Policy", action: openAgentSettings).buttonStyle(.link).font(.meta)
                }
            }
        }
        .padding(.leading, 34)
    }

    private func chipText(_ index: Int, _ citation: AgentCitation) -> String {
        let range = MediaTimeRange(locator: citation.locator).map { " · \($0.label)" } ?? ""
        return "\(index + 1)  \(citation.sourceName)\(range)"
    }
}

private struct Composer: View {
    @ObservedObject var model: ChatWorkbenchModel
    @FocusState private var focused: Bool

    private var tooLong: Bool { model.draft.count > ChatWorkbenchModel.maximumMessageCharacters }

    var body: some View {
        VStack(spacing: Spacing.xs) {
            HStack(alignment: .bottom, spacing: Spacing.s) {
                TextField("Ask Praxis…", text: $model.draft, axis: .vertical)
                    .textFieldStyle(.plain)
                    .lineLimit(1...8)
                    .focused($focused)
                    .onSubmit { if model.canSend { model.send() } }
                    .padding(.vertical, Spacing.xs)
                Button { model.send() } label: {
                    Image(systemName: "arrow.up.circle.fill").font(.system(size: 22))
                }
                .buttonStyle(.plain)
                .foregroundStyle(model.canSend ? Color.accentColor : Color.secondary.opacity(0.5))
                .keyboardShortcut(.return, modifiers: .command)
                .disabled(!model.canSend)
                .help("Send (Return). Option-Return adds a new line.")
                .accessibilityLabel("Send")
            }
            .padding(.horizontal, Spacing.m)
            .padding(.vertical, Spacing.s)
            .background(Surface.card, in: RoundedRectangle(cornerRadius: Radius.l, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: Radius.l, style: .continuous)
                .strokeBorder(focused ? Color.accentColor.opacity(0.6) : Surface.stroke))
            HStack {
                Text(model.selected.map { "Continuing with \($0.backendId)" } ?? "Answers cite your sources. Figures are checked against tool results.")
                Spacer()
                if tooLong {
                    Text("\(model.draft.count) / \(ChatWorkbenchModel.maximumMessageCharacters)").foregroundStyle(.orange)
                }
            }
            .font(.caption2).foregroundStyle(.tertiary)
        }
        .frame(maxWidth: 760)
        .padding(.horizontal, Spacing.xl)
        .padding(.top, Spacing.s)
        .padding(.bottom, Spacing.l)
        .frame(maxWidth: .infinity)
        .onAppear { focused = true }
    }
}

/// Inline Markdown for agent answers: emphasis, code and links, keeping the model's line breaks.
enum Markdown {
    static func inline(_ text: String) -> AttributedString {
        let options = AttributedString.MarkdownParsingOptions(interpretedSyntax: .inlineOnlyPreservingWhitespace,
                                                              failurePolicy: .returnPartiallyParsedIfPossible)
        return (try? AttributedString(markdown: text, options: options)) ?? AttributedString(text)
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

/// How a chat model reads in the picker: provider and model, never a bare icon.
enum ChatBackendLabel {
    static func of(_ backend: AgentBackendProfile) -> String {
        let provider = switch backend.kind {
        case "claude_code": "Claude Code"
        case "codex": "Codex"
        case "anthropic": "Anthropic"
        default: backend.isLocal ? "Local" : backend.backendId
        }
        let model = backend.model == "default" ? "plan default" : backend.model
        return "\(provider) · \(model)"
    }
}
