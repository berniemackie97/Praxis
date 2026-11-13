import Combine
import Foundation

/// Chat with the Praxis agent. A message starts a background run in the worker; the model polls
/// the run for progress events and the verified reply, so the single worker pipe stays free for
/// the rest of the app. One run per conversation at a time; runs can be canceled.
@MainActor
final class ChatWorkbenchModel: ObservableObject {
    /// The worker accepts requests up to 16 KB; leave room for the envelope and JSON escaping.
    static let maximumMessageCharacters = 12_000

    private let channel: WorkerChannel
    private var pollTask: Task<Void, Never>?

    @Published private(set) var backends: [AgentBackendProfile] = []
    @Published private(set) var conversations: [AgentConversation] = []
    @Published private(set) var selectedId: String?
    @Published private(set) var messages: [AgentMessage] = []
    @Published private(set) var runId: String?
    @Published private(set) var events: [AgentRunEvent] = []
    /// The latest finished reply per conversation, kept for its citations and answer check.
    @Published private(set) var replies: [String: AgentReply] = [:]
    @Published var draft = ""
    @Published var backendChoice = ""
    @Published private(set) var error: String? { didSet { if error != nil { notice = nil } } }
    @Published private(set) var notice: String? { didSet { if notice != nil { error = nil } } }

    init(channel: WorkerChannel) {
        self.channel = channel
    }

    var selected: AgentConversation? { conversations.first { $0.conversationId == selectedId } }
    var running: Bool { runId != nil }
    var latestReply: AgentReply? { selectedId.flatMap { replies[$0] } }
    var canSend: Bool {
        let text = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        return !running && !text.isEmpty && draft.count <= Self.maximumMessageCharacters && (selectedId != nil || !backendChoice.isEmpty)
    }

    /// The transcript as the owner reads it: their messages and the agent's answers; tool traffic is summarized.
    var transcript: [ChatLine] { ChatLine.lines(messages) }

    func refresh() {
        channel.send("agentBackends", as: AgentBackendsData.self, failed: report) { [weak self] data in
            guard let self else { return }
            backends = data.backends
            if backendChoice.isEmpty || !data.backends.contains(where: { $0.backendId == backendChoice && $0.enabled }) {
                backendChoice = data.backends.first(where: \.enabled)?.backendId ?? ""
            }
        }
        channel.send("agentConversations", ["limit": 100], as: AgentConversationsData.self, failed: report) { [weak self] data in
            self?.conversations = data.conversations
        }
    }

    func select(_ conversation: AgentConversation) {
        guard selectedId != conversation.conversationId else { return }
        selectedId = conversation.conversationId
        channel.reset("conversation")
        messages = []
        loadMessages()
    }

    func newConversation() {
        selectedId = nil
        messages = []
        events = []
        channel.reset("conversation")
    }

    func send() {
        guard canSend else { return }
        let text = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        if let selectedId {
            start(selectedId, text)
            return
        }

        let title = String(text.prefix(60))
        channel.send("agentConversationStart", ["backendId": backendChoice, "title": title], as: AgentConversation.self, failed: report) { [weak self] conversation in
            guard let self else { return }
            conversations.insert(conversation, at: 0)
            selectedId = conversation.conversationId
            start(conversation.conversationId, text)
        }
    }

    func cancel() {
        guard let runId else { return }
        channel.send("agentCancel", ["runId": runId], as: ChangedFlag.self, failed: report) { [weak self] _ in
            self?.notice = "Stopping…"
        }
    }

    private func start(_ conversationId: String, _ text: String) {
        channel.send("agentSend", ["conversationId": conversationId, "message": text], as: AgentRunStartedData.self, failed: report) { [weak self] data in
            guard let self else { return }
            draft = ""
            events = []
            runId = data.runId
            notice = nil
            loadMessages()
            poll(data.runId)
        }
    }

    private func poll(_ run: String) {
        pollTask?.cancel()
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 400_000_000)
                guard let self, self.runId == run else { return }
                let after = self.events.last?.sequence ?? 0
                self.channel.send("agentRun", ["runId": run, "after": after], as: AgentRunStatus.self, failed: self.report) { [weak self] status in
                    self?.apply(status)
                }
            }
        }
    }

    private func apply(_ status: AgentRunStatus) {
        guard status.runId == runId else { return }
        events.append(contentsOf: status.events.filter { event in !events.contains { $0.sequence == event.sequence } })
        guard !status.running else { return }
        pollTask?.cancel()
        pollTask = nil
        runId = nil
        if let reply = status.reply {
            replies[status.conversationId] = reply
        }

        switch status.state {
        case "failed": error = "The request failed: \(status.events.last?.detail.map(FailureCode.label) ?? "unknown error")."
        case "canceled": notice = "Stopped."
        default: notice = nil
        }

        if status.conversationId == selectedId { loadMessages() }
        refresh()
    }

    private func loadMessages() {
        guard let selectedId else { return }
        channel.send("agentConversation", ["conversationId": selectedId], as: AgentConversationData.self, scope: "conversation", failed: report) { [weak self] data in
            self?.messages = data.messages
        }
    }

    private func report(_ message: String) { error = message }
}

/// One line of the visible transcript.
struct ChatLine: Identifiable, Equatable {
    enum Kind: Equatable { case owner, agent, activity, summary }

    let id: Int
    let kind: Kind
    let text: String

    static func lines(_ messages: [AgentMessage]) -> [ChatLine] {
        var lines: [ChatLine] = []
        for (index, message) in messages.enumerated() {
            switch message.role {
            case "user" where !message.content.hasPrefix("Answer check failed."):
                lines.append(ChatLine(id: index, kind: .owner, text: message.content))
            case "assistant":
                if !message.toolCalls.isEmpty {
                    lines.append(ChatLine(id: index, kind: .activity, text: "Used " + message.toolCalls.map { ChatActivity.label($0.name) }.joined(separator: ", ")))
                }
                if !message.content.isEmpty {
                    lines.append(ChatLine(id: index * 1_000 + 1, kind: message.toolCalls.isEmpty ? .agent : .activity, text: message.content))
                }
            case "summary":
                lines.append(ChatLine(id: index, kind: .summary, text: "Earlier conversation summarized."))
            default:
                continue
            }
        }
        return lines
    }
}

/// Owner-facing names for agent tools and run events.
enum ChatActivity {
    static func label(_ tool: String) -> String {
        switch tool {
        case "knowledge_search": "search"
        case "knowledge_open": "read a source"
        case "knowledge_entity": "entity lookup"
        case "knowledge_related": "connections"
        case "knowledge_timeline": "timeline"
        case "knowledge_propose": "a knowledge suggestion"
        case "data_describe": "data overview"
        case "data_query": "exact data query"
        case "compute_evaluate": "calculator"
        case "artifact_create", "artifact_update": "a saved document"
        case "memory_recall": "your preferences"
        case "memory_remember": "remember a preference"
        case "intake_status": "intake status"
        case "intake_reprocess": "re-run intake"
        case "settings_read": "your settings"
        case "settings_propose": "a settings change"
        case "agent_delegate": "a helper model"
        default: tool.replacingOccurrences(of: "_", with: " ")
        }
    }

    static func progress(_ event: AgentRunEvent?) -> String {
        guard let event else { return "Starting…" }
        switch event.kind {
        case "thinking": return "Thinking…"
        case "tool_started": return "Using \(label(event.tool ?? ""))…"
        case "tool_finished": return "Finished \(label(event.tool ?? ""))."
        case "repairing": return "Correcting figures that no tool returned…"
        case "compacting": return "Summarizing the earlier conversation…"
        case "checking": return "Checking the answer…"
        default: return event.kind.replacingOccurrences(of: "_", with: " ")
        }
    }
}
