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
    private struct ConversationState {
        var draft = ""
        var useTools = true
        var busy = false
        var runId: String?
        var polling = false
        var pollStarted: ContinuousClock.Instant?
        var pollGeneration = 0
        var pollFailures = 0
        var transcriptDirty = false
        var lastTranscriptSync: ContinuousClock.Instant?
        var events: [AgentRunEvent] = []
        var reply: AgentReply?
        var runUserText: String?
        var runMessageCount = 0
        var feedback: Feedback?
    }

    private enum Feedback {
        case error(String)
        case notice(String)
    }

    // The empty key is the unsent new-chat composer. Saved conversations always use their UUID.
    @Published private var states: [String: ConversationState] = [:]
    private var pollTask: Task<Void, Never>?

    @Published private(set) var backends: [AgentBackendProfile] = []
    @Published private(set) var conversations: [AgentConversation] = []
    @Published private(set) var selectedId: String?
    @Published private(set) var recap: String?
    @Published private(set) var messages: [AgentMessage] = []
    @Published private(set) var brainRecalls: [BrainRecallReceipt] = []
    @Published private(set) var listError: String?
    @Published private(set) var backendChoice = ""
    @Published private(set) var modelChoice = ""
    @Published private(set) var effortChoice = ""
    @Published private(set) var contextChoice = "balanced"
    private var customModelChoice = false

    init(channel: WorkerChannel) {
        self.channel = channel
    }

    private var key: String { selectedId ?? "" }
    private var current: ConversationState { states[key] ?? ConversationState() }
    var selected: AgentConversation? { conversations.first { $0.conversationId == selectedId } }
    var running: Bool { current.runId != nil }
    var busy: Bool { current.busy || running }
    var events: [AgentRunEvent] { current.events }
    var latestReply: AgentReply? { current.reply }
    var error: String? {
        if case .error(let text) = current.feedback { return text }
        return nil
    }
    var notice: String? {
        if case .notice(let text) = current.feedback { return text }
        return nil
    }
    var draft: String {
        get { current.draft }
        set { states[key, default: ConversationState()].draft = newValue }
    }
    var useTools: Bool {
        get { current.useTools }
        set { states[key, default: ConversationState()].useTools = newValue }
    }
    var canSend: Bool {
        let text = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        return !busy && !text.isEmpty && draft.count <= Self.maximumMessageCharacters &&
            activeBackend?.enabled == true && !activeModel.isEmpty
    }

    func hasWork(_ id: String) -> Bool { states[id]?.busy == true || states[id]?.runId != nil }

    /// The transcript as the owner reads it: their messages and the agent's answers; tool traffic is summarized.
    var transcript: [ChatLine] {
        let state = current
        return ChatLine.lines(messages, after: state.runMessageCount,
                              prompt: state.busy || state.runId != nil ? nil : state.runUserText,
                              reply: state.reply)
    }

    var activeBackendId: String { selected?.backendId ?? backendChoice }
    var activeBackend: AgentBackendProfile? { backends.first { $0.backendId == activeBackendId } }
    var activeModel: String {
        selectedId == nil ? modelChoice : selected?.model ?? activeBackend?.model ?? ""
    }
    var activeEffort: String {
        if let selected { return (selected.model == nil ? activeBackend?.effort : selected.effort) ?? "" }
        return effortChoice
    }

    var activeContextPreset: String {
        selectedId == nil ? contextChoice : selected?.contextPreset ?? "balanced"
    }

    func chooseContextPreset(_ preset: String) {
        guard !busy, ["compact", "balanced", "expanded"].contains(preset) else { return }
        guard let id = selectedId else {
            contextChoice = preset
            return
        }

        states[id, default: ConversationState()].busy = true
        channel.send("agentConversationContext", ["conversationId": id, "contextPreset": preset],
                     as: AgentConversation.self, failed: { [weak self] message in
            self?.failOperation(id, message)
        }) { [weak self] conversation in
            self?.states[id]?.busy = false
            self?.updateConversation(conversation)
        }
    }

    func chooseBackend(_ backendId: String) {
        guard selectedId == nil, !busy,
              let profile = backends.first(where: { $0.backendId == backendId && $0.enabled }) else { return }
        backendChoice = backendId
        modelChoice = profile.model
        effortChoice = profile.effort ?? ""
        customModelChoice = false
    }

    func chooseModel(_ modelId: String, defaultEffort: String?) {
        setConfiguration(modelId, effort: defaultEffort ?? "")
    }

    func chooseEffort(_ effort: String) {
        setConfiguration(activeModel, effort: effort)
    }

    private func setConfiguration(_ model: String, effort: String) {
        guard !busy, !model.isEmpty, activeBackend?.enabled == true else { return }
        guard let id = selectedId else {
            modelChoice = model
            effortChoice = effort
            customModelChoice = true
            return
        }

        states[id, default: ConversationState()].busy = true
        states[id]?.feedback = nil
        var fields: [String: Any] = ["conversationId": id, "model": model]
        if !effort.isEmpty { fields["effort"] = effort }
        channel.send("agentConversationModel", fields, as: AgentConversation.self, failed: { [weak self] message in
            self?.failOperation(id, message)
        }) { [weak self] conversation in
            self?.states[id]?.busy = false
            self?.updateConversation(conversation)
        }
    }

    /// Asks the worker how to play a cited media source; the reply arrives on the main actor.
    func playback(for citation: AgentCitation, completion: @escaping (Result<MediaPlayback, MediaPlaybackError>) -> Void) {
        var fields: [String: Any] = ["sourceId": citation.sourceId, "itemId": citation.itemId, "revision": citation.revision]
        if let timebase = citation.playbackTimebase { fields["timebase"] = timebase }
        let sent = channel.send("mediaPlayback", fields, as: MediaPlaybackData.self,
                                failed: { message in completion(.failure(MediaPlaybackError(message: message))) }) { data in
            completion(.success(data.playback))
        }
        if !sent { completion(.failure(MediaPlaybackError(message: "The worker is not running."))) }
    }

    func saveLibraryNote(id: UUID, title: String, text: String, completion: @escaping (String?) -> Void) {
        channel.send("noteCapture", ["captureId": id.uuidString.lowercased(), "title": title, "text": text],
                     as: ChatNoteCaptureData.self, failed: { completion($0) }) { _ in completion(nil) }
    }

    func refresh() {
        channel.reset("chatBackends")
        channel.send("agentBackends", as: AgentBackendsData.self, scope: "chatBackends", failed: reportListError) { [weak self] data in
            guard let self else { return }
            let previous = backends.first(where: { $0.backendId == backendChoice })
            backends = data.backends
            if backendChoice.isEmpty || !data.backends.contains(where: { $0.backendId == backendChoice && $0.enabled }) {
                let preferred = UserDefaults.standard.string(forKey: "defaultChatBackend") ?? ""
                let connection = data.backends.first { $0.backendId == preferred && $0.enabled }
                    ?? data.backends.first(where: \.enabled)
                backendChoice = connection?.backendId ?? ""
                modelChoice = connection?.model ?? ""
                effortChoice = connection?.effort ?? ""
                customModelChoice = false
            } else if !customModelChoice, let changed = data.backends.first(where: { $0.backendId == backendChoice }),
                      previous?.model != changed.model || previous?.effort != changed.effort {
                modelChoice = changed.model
                effortChoice = changed.effort ?? ""
            }
        }
        refreshConversations()
        loadMessages()
        refreshRecap()
        for id in Array(states.keys) where states[id]?.runId != nil {
            states[id]?.pollFailures = 0
            states[id]?.polling = false
            states[id]?.pollStarted = nil
            states[id]?.pollGeneration += 1
        }
        ensurePolling()
    }

    private func refreshConversations() {
        channel.reset("chatList")
        channel.send("agentConversations", ["limit": 100], as: AgentConversationsData.self, scope: "chatList",
                     failed: reportListError) { [weak self] data in
            self?.conversations = data.conversations
            self?.listError = nil
        }
    }

    private func updateConversation(_ conversation: AgentConversation) {
        channel.reset("chatList")
        if let index = conversations.firstIndex(where: { $0.id == conversation.id }) {
            conversations[index] = conversation
        } else {
            conversations.insert(conversation, at: 0)
        }
    }

    func select(_ conversation: AgentConversation) {
        guard selectedId != conversation.id else { return }
        selectedId = conversation.id
        recap = nil
        messages = []
        brainRecalls = []
        loadMessages()
        refreshRecap()
    }

    func newConversation() {
        selectedId = nil
        recap = nil
        messages = []
        channel.reset("conversation")
    }

    func rename(_ conversation: AgentConversation, to title: String) {
        let id = conversation.id
        let normalized = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard (1...200).contains(normalized.count) else {
            feedback(.error("Use a conversation title of 1–200 characters."), for: id)
            return
        }
        channel.send("agentConversationRename", ["conversationId": id, "title": normalized],
                     as: ChangedFlag.self, failed: { [weak self] in self?.feedback(.error($0), for: id) }) { [weak self] result in
            if result.changed { self?.refreshConversations() }
            else { self?.feedback(.error("This conversation no longer exists."), for: id) }
        }
    }

    func delete(_ conversation: AgentConversation) {
        let id = conversation.id
        guard !hasWork(id) else { return }
        states[id, default: ConversationState()].busy = true
        channel.send("agentConversationDelete", ["conversationId": id], as: ChangedFlag.self,
                     failed: { [weak self] in self?.failOperation(id, $0) }) { [weak self] result in
            guard let self else { return }
            guard result.changed else {
                failOperation(id, "This conversation no longer exists.")
                return
            }

            channel.reset("chatList")
            conversations.removeAll { $0.id == id }
            states.removeValue(forKey: id)
            if selectedId == id { newConversation() }
        }
    }

    func prepareDraft(_ text: String) -> Bool {
        guard !busy else {
            feedback(.error("Finish or stop the current response before preparing a graph question."), for: key)
            return false
        }
        guard draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || draft == text else {
            feedback(.error("Your current draft was preserved. Save or send it before preparing a graph question."), for: key)
            return false
        }
        draft = String(text.prefix(Self.maximumMessageCharacters))
        return true
    }

    func send() {
        guard canSend else { return }
        let original = draft
        let tools = useTools
        let text = original.trimmingCharacters(in: .whitespacesAndNewlines)
        if let selectedId {
            start(selectedId, text, originalDraft: original, useTools: tools)
            return
        }

        states["", default: ConversationState()].busy = true
        feedback(nil, for: "")
        var fields: [String: Any] = ["backendId": backendChoice, "title": String(text.prefix(60)), "model": modelChoice]
        if !effortChoice.isEmpty { fields["effort"] = effortChoice }
        fields["contextPreset"] = contextChoice
        channel.send("agentConversationStart", fields, as: AgentConversation.self,
                     failed: { [weak self] in self?.failOperation("", $0) }) { [weak self] conversation in
            guard let self else { return }
            states[""]?.busy = false
            if states[""]?.draft == original { states[""]?.draft = "" }
            updateConversation(conversation)
            states[conversation.id] = ConversationState(draft: original, useTools: tools)
            if selectedId == nil { select(conversation) }
            start(conversation.id, text, originalDraft: original, useTools: tools)
        }
    }

    func cancel() {
        let id = key
        guard let runId = current.runId else { return }
        channel.send("agentCancel", ["runId": runId], as: ChangedFlag.self, failed: { [weak self] message in
            guard self?.states[id]?.runId == runId else { return }
            self?.feedback(.error(message), for: id)
        }) { [weak self] result in
            guard result.changed, self?.states[id]?.runId == runId else { return }
            self?.feedback(.notice("Stopping…"), for: id)
        }
    }

    func workerExited() {
        pollTask?.cancel()
        pollTask = nil
        for id in states.keys where hasWork(id) {
            states[id]?.busy = false
            states[id]?.runId = nil
            states[id]?.polling = false
            states[id]?.pollStarted = nil
            states[id]?.pollGeneration += 1
            feedback(.error("The worker stopped. Check this conversation before sending again."), for: id)
        }
    }

    private func start(_ id: String, _ text: String, originalDraft: String, useTools: Bool) {
        states[id, default: ConversationState()].busy = true
        states[id]?.runUserText = text
        states[id]?.runMessageCount = selectedId == id ? messages.count : 0
        states[id]?.reply = nil
        feedback(nil, for: id)
        channel.send("agentSend", ["conversationId": id, "message": text, "useTools": useTools], as: AgentRunStartedData.self,
                     failed: { [weak self] in self?.failOperation(id, $0) }) { [weak self] data in
            guard let self else { return }
            if states[id]?.draft == originalDraft { states[id]?.draft = "" }
            states[id]?.busy = false
            states[id]?.runId = data.runId
            states[id]?.events = []
            states[id]?.polling = false
            states[id]?.pollStarted = nil
            states[id]?.pollGeneration += 1
            states[id]?.pollFailures = 0
            states[id]?.transcriptDirty = false
            states[id]?.lastTranscriptSync = nil
            if selectedId == id { loadMessages() }
            ensurePolling()
        }
    }

    private func ensurePolling() {
        guard pollTask == nil, states.values.contains(where: { $0.runId != nil && $0.pollFailures < 3 }) else { return }
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(400))
                guard !Task.isCancelled, let self else { return }
                if !pollRuns() {
                    pollTask = nil
                    return
                }
            }
        }
    }

    static func pollExpired(_ started: ContinuousClock.Instant?, now: ContinuousClock.Instant) -> Bool {
        guard let started else { return true }
        return started.duration(to: now) >= .seconds(5)
    }

    private func pollRuns() -> Bool {
        let active = states.filter { $0.value.runId != nil && $0.value.pollFailures < 3 }
        let now = ContinuousClock.now
        for (id, state) in active {
            guard let run = state.runId else { continue }
            if selectedId == id, state.transcriptDirty,
               state.lastTranscriptSync.map({ $0.duration(to: now) >= .seconds(1) }) ?? true {
                states[id]?.transcriptDirty = false
                states[id]?.lastTranscriptSync = now
                loadMessages()
            }
            if state.polling {
                guard Self.pollExpired(state.pollStarted, now: now) else { continue }
                states[id]?.pollFailures += 1
                if states[id]?.pollFailures == 3 {
                    feedback(.error("Lost contact with this response. Refresh its status or try Stop."), for: id)
                    continue
                }
            }

            states[id]?.polling = true
            states[id]?.pollStarted = now
            states[id]?.pollGeneration += 1
            let generation = states[id]?.pollGeneration
            channel.send("agentRun", ["runId": run, "after": state.events.last?.sequence ?? 0],
                         as: AgentRunStatus.self, failed: { [weak self] message in
                guard let self, states[id]?.runId == run, states[id]?.pollGeneration == generation else { return }
                states[id]?.polling = false
                states[id]?.pollStarted = nil
                if message == "Unknown run." {
                    states[id]?.runId = nil
                    feedback(.error("This response is no longer available. Check the saved conversation before retrying."), for: id)
                    if selectedId == id { loadMessages() }
                } else {
                    states[id]?.pollFailures += 1
                    if states[id]?.pollFailures == 3 {
                        feedback(.error("Lost contact with this response. Refresh its status or try Stop."), for: id)
                    }
                }
            }) { [weak self] status in
                guard let self, status.conversationId == id, status.runId == run,
                      states[id]?.runId == run, states[id]?.pollGeneration == generation else { return }
                apply(status)
            }
        }
        return !active.isEmpty
    }

    var statusPaused: Bool { running && current.pollFailures >= 3 }
    var savedAnswerPendingStatus: Bool {
        guard running, let prompt = current.runUserText else { return false }
        let currentTurn = messages.dropFirst(min(current.runMessageCount, messages.count))
        guard let request = currentTurn.firstIndex(where: { $0.role == "user" && $0.content == prompt }) else { return false }
        return currentTurn[currentTurn.index(after: request)...].contains {
            $0.role == "assistant" && !$0.content.isEmpty && $0.toolCalls.isEmpty
        }
    }

    private func apply(_ status: AgentRunStatus) {
        let id = status.conversationId
        states[id]?.polling = false
        states[id]?.pollStarted = nil
        states[id]?.pollFailures = 0
        let lastSequence = states[id]?.events.last?.sequence ?? 0
        let newEvents = status.events.filter { $0.sequence > lastSequence }
        states[id]?.events.append(contentsOf: newEvents)
        if status.running {
            if newEvents.contains(where: { $0.kind == "tool_finished" }) {
                states[id]?.transcriptDirty = true
            }
            return
        }
        states[id]?.runId = nil
        states[id]?.reply = status.reply
        states[id]?.transcriptDirty = false

        switch status.state {
        case "failed": feedback(.error(ChatFailureText.describe(states[id]?.events.last?.detail)), for: id)
        case "canceled": feedback(.notice("Stopped."), for: id)
        default: feedback(nil, for: id)
        }

        if selectedId == id { loadMessages() }
        refreshConversations()
    }

    func refreshRecap() {
        guard let id = selectedId else { return }
        channel.send("agentRecap", ["conversationId": id], as: AgentRecapData.self, failed: { _ in }) { [weak self] data in
            guard let self, selectedId == id else { return }
            if recap != data.recap { recap = data.recap }
        }
    }

    private func loadMessages() {
        channel.reset("conversation")
        guard let id = selectedId else { return }
        channel.send("agentConversation", ["conversationId": id], as: AgentConversationData.self, scope: "conversation",
                     failed: { [weak self] in self?.feedback(.error($0), for: id) }) { [weak self] data in
            self?.messages = data.messages
            self?.brainRecalls = data.brainRecalls ?? []
        }
    }

    private func failOperation(_ id: String, _ message: String) {
        states[id]?.busy = false
        feedback(.error(message), for: id)
    }

    private func feedback(_ value: Feedback?, for id: String) {
        states[id, default: ConversationState()].feedback = value
    }

    private func reportListError(_ message: String) { listError = message }
}

/// Safe, actionable descriptions of bounded worker failure codes. Provider error text stays private.
enum ChatFailureText {
    static func describe(_ code: String?) -> String {
        switch code {
        case "model_timeout": return "The model timed out. Try a shorter request or a faster model, then send again."
        case "model_unavailable": return "The model is unreachable. Check the connection in Models, then retry."
        case "model_unauthenticated": return "The provider sign-in expired. Sign in again under Models."
        case "model_quota_exhausted": return "The provider reached its usage limit. Choose another model if the connection supports it, or wait for the limit to reset."
        case "model_chat_unsupported": return "This model cannot generate chat replies. Choose a chat model."
        case "model_missing": return "The selected model is unavailable. Review its configuration under Models."
        case "backend_configuration_error": return "This chat model is not configured correctly. Review Models before retrying."
        case "model_approval_required": return "Codex could not request an approval in Praxis. Try a read-only question or use the provider directly for the action."
        case "model_rejected": return "The connection rejected the request. For private Ollama Chat, choose an installed local model."
        case "model_invalid_response": return "The provider did not return a usable answer. Try again or choose another model."
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
    var citations: [AgentCitation] = []

    static func lines(_ messages: [AgentMessage], after start: Int = 0,
                      prompt: String? = nil, reply: AgentReply? = nil) -> [ChatLine] {
        var lines: [ChatLine] = []
        for (index, message) in messages.enumerated() {
            switch message.role {
            case "user" where !message.content.hasPrefix("Answer check failed."):
                lines.append(ChatLine(id: index * 2, kind: .owner, text: message.content))
            case "assistant":
                if !message.toolCalls.isEmpty {
                    lines.append(ChatLine(id: index * 2, kind: .activity, text: "Used " + message.toolCalls.map { ChatActivity.label($0.name) }.joined(separator: ", ")))
                } else if !message.content.isEmpty {
                    lines.append(ChatLine(id: index * 2 + 1, kind: .agent, text: message.content, citations: message.citations ?? []))
                }
            case "summary":
                lines.append(ChatLine(id: index * 2, kind: .summary, text: "Earlier conversation summarized."))
            default:
                continue
            }
        }
        if let reply, let prompt, !reply.text.isEmpty {
            let currentTurn = messages.dropFirst(min(start, messages.count))
            if !currentTurn.contains(where: { $0.role == "assistant" && $0.content == reply.text && $0.toolCalls.isEmpty }) {
                if !currentTurn.contains(where: { $0.role == "user" && $0.content == prompt }) {
                    lines.append(ChatLine(id: messages.count * 2, kind: .owner, text: prompt))
                }
                lines.append(ChatLine(id: messages.count * 2 + 1, kind: .agent, text: reply.text, citations: reply.citations))
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
        case "media_find": "video and recording moments"
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
        case "recalling_brain": return "Finding relevant library notes…"
        case "recalling_media": return "Finding playable moments…"
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
