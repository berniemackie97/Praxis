import SwiftUI

/// Conversations with the Praxis agent: answers grounded in the owner's sources through tools,
/// with numbered citations, the answer check's verdict, and anything disclosure policy withheld.
struct ChatView: View {
    @ObservedObject var model: ChatWorkbenchModel
    @ObservedObject var setup: ModelSetupModel
    let openAgentSettings: () -> Void
    @State private var showingRecall = false

    private var installed: [InstalledModel] { setup.machine?.installed ?? [] }
    private var localModel: InstalledModel? { InstalledModel.matching(model.activeModel, in: installed) }
    private var supportsChat: Bool {
        model.activeBackendId != ModelTool.ollama || (localModel?.supportsChat ?? InstalledModel.mayBeChat(model.activeModel))
    }
    private var supportsTools: Bool {
        model.activeBackendId != ModelTool.ollama ||
            (localModel?.capabilities?.tools ?? InstalledModel.allowsToolsWithoutMetadata(model.activeModel))
    }

    private var modelOptions: [ModelChoice] {
        let available = setup.models(for: model.activeBackendId, installed: installed)
        if supportsChat && !model.activeModel.isEmpty && !available.contains(where: { $0.id == model.activeModel }) {
            return available + [ModelChoice(id: model.activeModel, label: model.activeModel, efforts: [], defaultEffort: nil)]
        }
        return available
    }

    private var effortOptions: [String] {
        guard let backend = model.activeBackend,
              backend.kind == "codex" || backend.kind == "claude_code",
              let selected = modelOptions.first(where: { $0.id == model.activeModel }) else { return [] }
        return Array(Set(selected.efforts + (model.activeEffort.isEmpty ? [] : [model.activeEffort]))).sorted()
    }

    var body: some View {
        Group {
            if model.backends.isEmpty && model.conversations.isEmpty {
                EmptyState(systemImage: "cpu",
                           title: "Connect a model to start",
                           message: "Praxis can chat through a local model, an API key, or your Claude Code or Codex plan. Messages go to the chosen model. Your disclosure policy controls source access.",
                           actionTitle: "Set Up Models…", action: openAgentSettings)
            } else {
                conversations
            }
        }
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Button { model.newConversation() } label: { Label("New Conversation", systemImage: "square.and.pencil") }
                    .help("New conversation (⌘N)")
                Button { showingRecall = true } label: { Label("Brain Context", systemImage: "brain") }
                    .disabled(model.selectedId == nil || model.brainRecalls.isEmpty)
            }
        }
        .sheet(isPresented: $showingRecall) { recallHistory }
        .onAppear {
            model.refresh()
            setup.refresh()
        }
    }

    private var recallHistory: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            Text("Brain Context").font(.title2)
            Text("Context budget: \(model.activeContextPreset.capitalized). Change it beside the model picker.")
                .font(.callout)
            Text("Recent recall decisions for this chat. These are candidate excerpts, not proof the answer used them.")
                .foregroundStyle(.secondary)
            List(model.brainRecalls) { receipt in
                VStack(alignment: .leading, spacing: Spacing.xs) {
                    Text("\(DateText.short(receipt.createdUtc)) · \(receipt.backendId)").font(.headline)
                    Text(recallLabel(receipt.outcome)).font(.callout)
                    Text("\(receipt.considered) candidate sources · \(receipt.withheld) withheld").font(.caption)
                    ForEach(Array(receipt.selected.enumerated()), id: \.offset) { _, citation in
                        Text("\(citation.sourceName) · revision \(citation.revision)").font(.caption).textSelection(.enabled)
                    }
                }
            }
            HStack {
                Spacer()
                Button("Done") { showingRecall = false }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(Spacing.xl)
        .frame(minWidth: 570, minHeight: 410)
    }

    private func recallLabel(_ outcome: String) -> String {
        switch outcome {
        case "full_match": "Selected excerpts matching the query"
        case "focused_match": "Selected excerpts matching multiple terms"
        case "withheld": "Relevant excerpts were withheld by policy"
        case "weak_matches": "No strong enough match to include"
        case "no_query": "No useful lookup terms in the message"
        case "not_needed": "No Brain lookup needed for this request"
        default: "No matching indexed excerpts"
        }
    }

    private var configuration: some View {
        HStack(spacing: Spacing.m) {
            if model.selectedId == nil {
                Picker("Connection", selection: Binding(
                    get: { model.backendChoice }, set: { model.chooseBackend($0) })) {
                    ForEach(model.backends.filter(\.enabled)) { backend in
                        Text(backend.backendId).tag(backend.backendId)
                    }
                }
            } else if let conversation = model.selected {
                Label(conversation.backendId, systemImage: "cpu")
                    .foregroundStyle(.secondary).lineLimit(1)
                    .help("The connection and its disclosure rules stay fixed for this conversation.")
            }
            if !modelOptions.isEmpty {
                Picker("Model", selection: Binding(
                    get: { model.activeModel },
                    set: { choice in
                        model.chooseModel(choice, defaultEffort: modelOptions.first { $0.id == choice }?.defaultEffort)
                    })) {
                    if !supportsChat { Text("Choose a chat model").tag(model.activeModel).disabled(true) }
                    ForEach(modelOptions) { Text($0.label).tag($0.id) }
                }
            }
            if !effortOptions.isEmpty {
                Picker("Effort", selection: Binding(get: { model.activeEffort }, set: { model.chooseEffort($0) })) {
                    Text("Default").tag("")
                    ForEach(effortOptions, id: \.self) { Text($0.capitalized).tag($0) }
                }
            }
            Picker("Context", selection: Binding(get: { model.activeContextPreset }, set: { model.chooseContextPreset($0) })) {
                Text("Compact").tag("compact")
                Text("Balanced").tag("balanced")
                Text("Expanded").tag("expanded")
            }
            .help("Adjust how much chat history and how many Brain excerpts Praxis sends. The model’s token window and privacy rules do not change.")
            Spacer(minLength: 0)
        }
        .font(.callout)
        .controlSize(.small)
        .disabled(model.busy || model.activeBackend?.enabled == false)
        .help("Model and effort changes take effect on the next message.")
        .padding(.horizontal, Spacing.l)
        .padding(.vertical, Spacing.s)
    }

    private var conversations: some View {
        MasterDetail(listWidth: (200, 240, 340)) {
            ConversationList(model: model)
        } detail: {
            VStack(spacing: 0) {
                if model.backends.isEmpty {
                    EmptyState(systemImage: "cpu",
                               title: "Connect a model to start",
                               message: "Praxis can chat through a local model, an API key, or your Claude Code or Codex plan. Messages go to the chosen model. Your disclosure policy controls source access.",
                               actionTitle: "Set Up Models…", action: openAgentSettings)
                } else {
                    configuration
                    Divider()
                    if let recap = model.recap {
                        DisclosureGroup("Chat recap") {
                            Text(recap).textSelection(.enabled)
                        }
                        .font(.callout)
                        .padding(.horizontal, Spacing.l)
                        .padding(.vertical, Spacing.s)
                    }
                    Transcript(model: model, openAgentSettings: openAgentSettings)
                    Composer(model: model, supportsChat: supportsChat, supportsTools: supportsTools)
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
                    HStack {
                        Text(conversation.title).font(.rowTitle).lineLimit(2)
                        if model.hasWork(conversation.id) {
                            ProgressView().controlSize(.mini)
                                .accessibilityLabel("Conversation in progress")
                        }
                    }
                    Text("\(conversation.backendId) · \(conversation.model ?? "default") · \(DateText.relative(conversation.updatedUtc))")
                        .font(.meta).foregroundStyle(.secondary).lineLimit(1)
                }
                .padding(.vertical, Spacing.xxs)
                .tag(conversation.conversationId)
                .contextMenu {
                    Button {
                        renaming = conversation
                        renameTitle = conversation.title
                    } label: {
                        Label("Rename…", systemImage: "pencil")
                    }
                    Button(role: .destructive) {
                        deleting = conversation
                    } label: {
                        Label("Delete…", systemImage: "trash")
                    }
                    .disabled(model.hasWork(conversation.id))
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
            Text("The conversation, recap and tool-call history will be deleted. Separately saved knowledge, memories and artifacts stay available.")
        }
        .safeAreaInset(edge: .bottom) {
            if let error = model.listError {
                FeedbackLine(error: error, notice: nil).padding(Spacing.s)
            }
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
                        VStack(alignment: .leading, spacing: Spacing.m) {
                            ChatBubble(line: line)
                            ForEach(InlineChatClips.cited(in: line.text, citations: line.citations), id: \.number) { clip in
                                MediaClipCard(number: clip.number, citation: clip.citation, range: clip.range, model: model)
                            }
                        }.id(line.id)
                    }
                    if model.running {
                        Thinking(model: model).id("thinking")
                    } else if let reply = model.latestReply, !model.transcript.isEmpty {
                        ReplyDetails(reply: reply, model: model, openAgentSettings: openAgentSettings)
                    }
                    FeedbackLine(error: model.error, notice: model.notice).id("chat-feedback")
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
            .onChange(of: model.error) { error in
                if error != nil { proxy.scrollTo("chat-feedback", anchor: .bottom) }
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
                Text("Ask a question or start with one of these prompts.")
                    .font(.callout).foregroundStyle(.secondary)
            }
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 240), spacing: Spacing.m)], spacing: Spacing.m) {
                ForEach(Self.prompts, id: \.1) { symbol, prompt in
                    Button {
                        if model.prepareDraft(prompt) { model.useTools = true }
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
            Text(model.statusPaused ? "Status paused" :
                 model.savedAnswerPendingStatus ? "Answer saved. Confirming completion…" : ChatActivity.progress(model.events.last))
                .font(.callout).foregroundStyle(.secondary)
            if model.statusPaused { Button("Refresh Status") { model.refresh() } }
            Spacer()
            Button("Stop", role: .cancel) { model.cancel() }
                .controlSize(.small)
                .keyboardShortcut(".", modifiers: .command)
        }
        .padding(.leading, 34)
    }
}

enum CitationMarkers {
    private static let marker = try! NSRegularExpression(pattern: #"(?<![\p{L}\p{N}_])\[(\d{1,3})\](?!\()"#)

    static func indices(in text: String, count: Int) -> [Int] {
        guard count > 0 else { return [] }
        let content = text as NSString
        let matches = marker.matches(in: text, range: NSRange(location: 0, length: content.length))
        return Set(matches.compactMap { match -> Int? in
            guard let number = Int(content.substring(with: match.range(at: 1))), number >= 1, number <= count else { return nil }
            return number - 1
        }).sorted()
    }
}

enum InlineChatClips {
    static func cited(in text: String, citations: [AgentCitation]) -> [(number: Int, citation: AgentCitation, range: MediaTimeRange)] {
        Array(CitationMarkers.indices(in: text, count: citations.count).compactMap { index in
            let citation = citations[index]
            guard let range = MediaTimeRange(locator: citation.locator) else { return nil }
            return (index + 1, citation, range)
        }.prefix(3))
    }
}

private struct ReplyDetails: View {
    let reply: AgentReply
    @ObservedObject var model: ChatWorkbenchModel
    let openAgentSettings: () -> Void
    @State private var showingSources = false
    @State private var showingSaveNote = false

    private var citedIndices: [Int] { CitationMarkers.indices(in: reply.text, count: reply.citations.count) }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.s) {
            Button {
                showingSaveNote = true
            } label: {
                Label("Save as Topic Note…", systemImage: "square.and.pencil")
            }
            .buttonStyle(.link)
            .font(.meta)
            .sheet(isPresented: $showingSaveNote) {
                ChatNoteSheet(model: model, draft: reply.text)
            }
            if !citedIndices.isEmpty {
                DisclosureGroup(Count.label(citedIndices.count, "citation"), isExpanded: $showingSources) {
                    VStack(alignment: .leading, spacing: Spacing.s) {
                        ForEach(citedIndices, id: \.self) { index in
                            let citation = reply.citations[index]
                            Label(chipText(index, citation), systemImage: SourceSymbol.of(citation.sourceName))
                                .lineLimit(1)
                                .help("[\(index + 1)] \(citation.sourceName) · \(citation.locator)")
                        }
                    }
                    .padding(.top, Spacing.s)
                }
                .font(.callout)
            }
            if reply.toolsUnavailable == true {
                Text(reply.citations.isEmpty ? "This model replied without library tools." : "Read-only Brain context was available for this reply.")
                    .font(.meta).foregroundStyle(.secondary)
            }
            if reply.inputTokens > 0 || reply.outputTokens > 0 {
                Text("Reported tokens: \(reply.inputTokens.formatted()) in · \(reply.outputTokens.formatted()) out")
                    .font(.meta).foregroundStyle(.secondary)
                    .help("Provider-reported usage. CLI counts can include cached context and provider overhead; counts are not directly comparable or a subscription balance.")
            }
            if !reply.check.clean {
                Banner(text: AnswerCheckText.describe(reply.check), tone: .warning, systemImage: "exclamationmark.triangle.fill")
            }
            if let omitted = reply.omittedMessages, omitted > 0 {
                Label("\(omitted) earlier conversation messages were not sent to this model for this response. They remain saved in the conversation.",
                      systemImage: "text.alignleft")
                    .font(.meta).foregroundStyle(.secondary)
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
    let supportsChat: Bool
    let supportsTools: Bool
    @FocusState private var focused: Bool

    private var canSend: Bool { model.canSend && supportsChat }
    private var tooLong: Bool { model.draft.count > ChatWorkbenchModel.maximumMessageCharacters }

    var body: some View {
        VStack(spacing: Spacing.xs) {
            HStack(alignment: .bottom, spacing: Spacing.s) {
                TextField("Ask Praxis…", text: $model.draft, axis: .vertical)
                    .textFieldStyle(.plain)
                    .lineLimit(1...8)
                    .focused($focused)
                    .onSubmit { if canSend { model.send() } }
                    .padding(.vertical, Spacing.xs)
                Button { model.send() } label: {
                    Image(systemName: "arrow.up.circle.fill").font(.system(size: 22))
                }
                .buttonStyle(.plain)
                .foregroundStyle(canSend ? Color.accentColor : Color.secondary.opacity(0.5))
                .keyboardShortcut(.return, modifiers: .command)
                .disabled(!canSend)
                .help("Send (Return). Option-Return adds a new line.")
                .accessibilityLabel("Send")
            }
            .padding(.horizontal, Spacing.m)
            .padding(.vertical, Spacing.s)
            .background(Surface.card, in: RoundedRectangle(cornerRadius: Radius.l, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: Radius.l, style: .continuous)
                .strokeBorder(focused ? Color.accentColor.opacity(0.6) : Surface.stroke))
            HStack {
                if !supportsChat {
                    Text("Choose a model that supports chat.")
                } else if supportsTools {
                    Toggle("Allow tools", isOn: $model.useTools)
                        .toggleStyle(.checkbox)
                        .controlSize(.small)
                        .disabled(model.busy)
                        .help("Turn off tool calls. Relevant Brain context is supplied automatically when disclosure rules allow it.")
                } else {
                    Text("Brain context · tool calls unavailable")
                }
                if model.activeBackend?.isLocal == false {
                    Text("Messages go to \(model.activeBackendId).")
                }
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

private struct ChatNoteSheet: View {
    let model: ChatWorkbenchModel
    @Environment(\.dismiss) private var dismiss
    @State private var title = ""
    @State private var text: String
    @State private var saving = false
    @State private var error: String?
    @State private var captureId = UUID()

    init(model: ChatWorkbenchModel, draft: String) {
        self.model = model
        _text = State(initialValue: String(draft.prefix(8_000)))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            Text("Save as Topic Note").font(.headline)
            Text("Keep the decision or finding you want to reuse. This note enters your Primary Inbox and follows its intake policy.")
                .font(.callout).foregroundStyle(.secondary)
            TextField("Topic and decision title", text: $title).disabled(saving)
            TextEditor(text: $text).font(.body).frame(minHeight: 180)
                .border(Surface.stroke).disabled(saving)
            FeedbackLine(error: error, notice: nil)
            HStack {
                Button("Cancel") { dismiss() }.disabled(saving)
                Spacer()
                Button(saving ? "Saving…" : "Save to Inbox") {
                    saving = true
                    model.saveLibraryNote(id: captureId, title: title, text: text) { message in
                        saving = false
                        if let message { error = message } else { dismiss() }
                    }
                }
                .buttonStyle(.borderedProminent)
                .disabled(saving || title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                          title.utf16.count > 80 || text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                          text.utf16.count > 8_000)
            }
        }
        .padding(Spacing.xl)
        .frame(minWidth: 480)
        .interactiveDismissDisabled(saving)
        .onChange(of: title) { _ in captureId = UUID() }
        .onChange(of: text) { _ in captureId = UUID() }
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

