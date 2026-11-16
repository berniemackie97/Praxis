import Foundation

/// A configured chat backend. `backendId` is the provider name disclosure rules use.
struct AgentBackendProfile: Codable, Identifiable, Equatable {
    static let kinds = ["openai_compatible", "anthropic", "claude_code", "codex"]

    let backendId: String
    let kind: String
    let endpoint: String?
    let model: String
    let keyAccount: String?
    let executable: String?
    let enabled: Bool
    var effort: String?
    var id: String { backendId }

    /// Mirrors the worker: only a loopback OpenAI-compatible endpoint keeps content on this Mac.
    var isLocal: Bool {
        guard kind == "openai_compatible", let endpoint, let host = URL(string: endpoint)?.host else { return false }
        return host == "127.0.0.1" || host == "localhost" || host == "::1"
    }

    var kindLabel: String {
        switch kind {
        case "openai_compatible": isLocal ? "Local (OpenAI-compatible)" : "OpenAI-compatible API"
        case "anthropic": "Anthropic API"
        case "claude_code": "Claude Code (plan)"
        case "codex": "Codex (plan)"
        default: kind
        }
    }
}

struct AgentConversation: Decodable, Identifiable, Equatable {
    let conversationId: String
    let backendId: String
    let title: String
    let createdUtc: String
    let updatedUtc: String
    let model: String?
    let effort: String?
    var id: String { conversationId }

    init(conversationId: String, backendId: String, title: String, createdUtc: String, updatedUtc: String,
         model: String? = nil, effort: String? = nil) {
        self.conversationId = conversationId
        self.backendId = backendId
        self.title = title
        self.createdUtc = createdUtc
        self.updatedUtc = updatedUtc
        self.model = model
        self.effort = effort
    }
}

struct AgentToolCall: Decodable, Equatable {
    let callId: String
    let name: String
    let argumentsJson: String
}

struct AgentCitation: Decodable, Equatable, Identifiable {
    let sourceId: String
    let itemId: String
    let revision: Int
    let locator: String
    let sourceName: String
    var id: String { "\(itemId)-\(revision)-\(locator)" }
}

struct AgentMessage: Decodable, Equatable {
    let role: String
    let content: String
    let toolCalls: [AgentToolCall]
    let toolCallId: String?
}

struct AnswerCheck: Decodable, Equatable {
    let unsupportedFigures: [String]
    let missingCitations: Bool
    let repaired: Bool
    var clean: Bool { unsupportedFigures.isEmpty && !missingCitations }
}

struct AgentReply: Decodable, Equatable {
    let conversationId: String
    let text: String
    let stopReason: String
    let toolCalls: [AgentToolCall]
    let citations: [AgentCitation]
    let withheldResults: Int
    let check: AnswerCheck
    let inputTokens: Int
    let outputTokens: Int
    let omittedMessages: Int?
    let toolsUnavailable: Bool?
}

struct AgentRunEvent: Decodable, Equatable, Identifiable {
    let sequence: Int
    let kind: String
    let tool: String?
    let detail: String?
    let atUtc: String
    var id: Int { sequence }
}

struct AgentRunStatus: Decodable, Equatable {
    let runId: String
    let conversationId: String
    let state: String
    let events: [AgentRunEvent]
    let reply: AgentReply?
    var running: Bool { state == "running" }
}

struct AgentToolCallRecord: Decodable, Equatable {
    let callId: String
    let tool: String
    let tier: String
    let outcome: String
    let citations: Int
    let withheld: Int
}

struct AgentProposal: Decodable, Identifiable, Equatable {
    let proposalId: String
    let kind: String
    let payloadJson: String
    let rationale: String
    let status: String
    let createdUtc: String
    var id: String { proposalId }
}

struct AgentMemory: Decodable, Identifiable, Equatable {
    let memoryId: String
    let text: String
    let status: String
    let createdUtc: String
    var id: String { memoryId }
}

struct ChatNoteCaptureData: Decodable {
    struct Capture: Decodable { let sourcePath: String }
    let capture: Capture
}

struct AgentBackendsData: Decodable { let backends: [AgentBackendProfile] }
struct AgentConversationsData: Decodable { let conversations: [AgentConversation] }
struct BrainRecallReceipt: Decodable, Identifiable {
    let id: String
    let backendId: String
    let outcome: String
    let considered: Int
    let withheld: Int
    let selected: [AgentCitation]
    let createdUtc: String
}

struct CapabilityItem: Decodable, Identifiable {
    let id: String
    let operation: String
    let target: String?
    let description: String
    let status: String
    let detail: String?

    var title: String {
        operation == "terminal" ? "Run a terminal command" : "Open a browser address"
    }
}

struct CapabilityRequestsData: Decodable { let requests: [CapabilityItem] }

struct AgentConversationData: Decodable {
    let conversation: AgentConversation
    let messages: [AgentMessage]
    let toolCalls: [AgentToolCallRecord]
    let brainRecalls: [BrainRecallReceipt]?
}
struct AgentRecapData: Decodable { let recap: String? }
struct AgentRunStartedData: Decodable { let runId: String }
struct AgentProposalsData: Decodable { let proposals: [AgentProposal] }
struct AgentMemoriesData: Decodable { let memories: [AgentMemory] }
struct ChangedFlag: Decodable { let changed: Bool }
