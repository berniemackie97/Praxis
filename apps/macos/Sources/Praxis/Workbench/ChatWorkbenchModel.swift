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
    private var pollInFlight = false

    @Published private(set) var backends: [AgentBackendProfile] = []
    @Published private(set) var conversations: [AgentConversation] = []
    @Published private(set) var selectedId: String?
    @Published private(set) var messages: [AgentMessage] = []
    @Published private(set) var runId: String?
    @Published private(set) var events: [AgentRunEvent] = []
    /// The latest finished reply per conversation, kept for its citations and answer check.
    @Published private(set) var replies: [String: AgentReply] = [:]
    @Published var draft = ""
    @Published private(set) var backendChoice = ""
    @Published private(set) var modelChoice = ""
    @Published private(set) var effortChoice = ""
    private var customModelChoice = false
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
        return !running && !text.isEmpty && draft.count <= Self.maximumMessageCharacters &&
            (selectedId != nil || (!backendChoice.isEmpty && !modelChoice.isEmpty))
    }

    /// The transcript as the owner reads it: their messages and the agent's answers; tool traffic is summarized.
    var transcript: [ChatLine] { ChatLine.lines(messages) }

    func chooseBackend(_ backendId: String) {
        guard let profile = backends.first(where: { $0.backendId == backendId && $0.enabled }) else { return }
        backendChoice = backendId
        modelChoice = profile.model
        effortChoice = profile.effort ?? ""
        customModelChoice = false
    }

    func chooseModel(_ modelId: String, defaultEffort: String?) {
        guard !modelId.isEmpty else { return }
        modelChoice = modelId
        effortChoice = defaultEffort ?? ""
        customModelChoice = true
    }

    func chooseEffort(_ effort: String) {
        effortChoice = effort
        customModelChoice = true
    }

    /// Asks the worker how to play a cited media source; the reply arrives on the main actor.
    func playback(for sourceId: String, completion: @escaping (Result<MediaPlayback, MediaPlaybackError>) -> Void) {
        let sent = channel.send("mediaPlayback", ["sourceId": sourceId], as: MediaPlaybackData.self,
                                failed: { message in completion(.failure(MediaPlaybackError(message: message))) }) { data in
            completion(.success(data.playback))
        }
        if !sent { completion(.failure(MediaPlaybackError(message: "The worker is not running."))) }
    }

    func refresh() {
        channel.send("agentBackends", as: AgentBackendsData.self, failed: report) { [weak self] data in
            guard let self else { return }
            let previous = backends.first(where: { $0.backendId == backendChoice })
            backends = data.backends
            if backendChoice.isEmpty || !data.backends.contains(where: { $0.backendId == backendChoice && $0.enabled }) {
                let preferred = UserDefaults.standard.string(forKey: "defaultChatBackend") ?? ""
                let selected = data.backends.first { $0.backendId == preferred && $0.enabled }
                    ?? data.backends.first(where: \.enabled)
                if let selected { chooseBackend(selected.backendId) }
            } else if !customModelChoice, let changed = data.backends.first(where: { $0.backendId == backendChoice }),
                      previous?.model != changed.model || previous?.effort != changed.effort {
                modelChoice = changed.model
                effortChoice = changed.effort ?? ""
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
        if !backendChoice.isEmpty { chooseBackend(backendChoice) }
        selectedId = nil
        messages = []
        events = []
        channel.reset("conversation")
    }

    func rename(_ conversation: AgentConversation, to title: String) {
        let normalized = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard (1...200).contains(normalized.count) else {
            error = "Use a conversation title of 1–200 characters."
            return
        }
        channel.send("agentConversationRename", ["conversationId": conversation.conversationId, "title": normalized],
                     as: ChangedFlag.self, failed: report) { [weak self] result in
            if result.changed { self?.refresh() }
            else { self?.error = "This conversation no longer exists." }
        }
    }

    func delete(_ conversation: AgentConversation) {
        channel.send("agentConversationDelete", ["conversationId": conversation.conversationId],
                     as: ChangedFlag.self, failed: report) { [weak self] result in
            guard let self else { return }
            if !result.changed {
                error = "This conversation no longer exists."
                return
            }
            conversations.removeAll { $0.conversationId == conversation.conversationId }
            replies.removeValue(forKey: conversation.conversationId)
            if selectedId == conversation.conversationId {
                selectedId = nil
                messages = []
                events = []
                channel.reset("conversation")
            }
            notice = "Conversation deleted. Independently saved knowledge and artifacts remain available."
        }
    }

    func prepareDraft(_ text: String) -> Bool {
        guard !running else {
            error = "Finish or cancel the current response before preparing a graph question."
            return false
        }
        guard draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || draft == text else {
            error = "Your current draft was preserved. Save or send it before preparing a graph question."
            return false
        }
        draft = String(text.prefix(Self.maximumMessageCharacters))
        return true
    }

    func send() {
        guard canSend else { return }
        let text = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        if let selectedId {
            start(selectedId, text)
            return
        }

        let title = String(text.prefix(60))
        var fields: [String: Any] = ["backendId": backendChoice, "title": title, "model": modelChoice]
        if !effortChoice.isEmpty { fields["effort"] = effortChoice }
        channel.send("agentConversationStart", fields, as: AgentConversation.self, failed: report) { [weak self] conversation in
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
        pollInFlight = false
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 400_000_000)
                guard let self, self.runId == run else { return }
                guard !self.pollInFlight else { continue }
                self.pollInFlight = true
                let after = self.events.last?.sequence ?? 0
                self.channel.send("agentRun", ["runId": run, "after": after], as: AgentRunStatus.self, failed: { [weak self] message in
                    self?.pollInFlight = false
                    self?.report(message)
                }) { [weak self] status in
                    self?.pollInFlight = false
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
        pollInFlight = false
        runId = nil
        if let reply = status.reply {
            replies[status.conversationId] = reply
        }

        switch status.state {
        case "failed": error = ChatFailureText.describe(status.events.last?.detail)
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

/// Safe, actionable descriptions of bounded worker failure codes. Provider error text stays private.
enum ChatFailureText {
    static func describe(_ code: String?) -> String {
        switch code {
        case "model_timeout": return "The model timed out. Try a shorter request or a faster model, then send again."
        case "model_unavailable": return "The model is unreachable. Check the connection in Models, then retry."
        case "model_unauthenticated": return "The provider sign-in expired. Sign in again under Models."
        case "model_quota_exhausted": return "The provider reached its usage limit. Choose an available model for a new conversation."
        case "model_missing": return "The selected model is unavailable. Review its configuration under Models."
        case "backend_configuration_error": return "This chat model is not configured correctly. Review Models before retrying."
        case "invalid_request": return "The worker rejected this request. Try a shorter message."
        default: return "Chat failed unexpectedly. No answer was saved; the message remains in this conversation."
        }
    }
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
        case "context_limited": return "Selecting recent conversation context…"
        case "checking": return "Checking the answer…"
        default: return event.kind.replacingOccurrences(of: "_", with: " ")
        }
    }
}
