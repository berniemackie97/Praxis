import Foundation
import Testing
@testable import Praxis

@Suite("Agent chat, approvals and privacy settings")
struct AgentPresentationTests {
    @Test func runStatusWithVerifiedReplyDecodesFromWorkerJson() throws {
        let json = """
            {"ok":true,"data":{"runId":"r1","conversationId":"c1","state":"completed",
             "events":[{"sequence":1,"kind":"thinking","tool":null,"detail":null,"atUtc":"2026-11-13T12:00:00+00:00"},
                       {"sequence":2,"kind":"tool_started","tool":"data_query","detail":null,"atUtc":"2026-11-13T12:00:01+00:00"}],
             "reply":{"conversationId":"c1","text":"You spent $1,270.20 [1].","stopReason":"completed",
               "toolCalls":[{"callId":"a","name":"data_query","argumentsJson":"{}"}],
               "citations":[{"sourceId":"s","itemId":"i","revision":1,"locator":"stmttrn:2","sourceName":"download.qfx"}],
               "withheldResults":2,"check":{"unsupportedFigures":[],"missingCitations":false,"repaired":true,"clean":true},
               "inputTokens":900,"outputTokens":40,"omittedMessages":3}}}
            """
        let reply = try JSONDecoder().decode(WorkerReply<AgentRunStatus>.self, from: Data(json.utf8))
        let status = try #require(reply.data)
        #expect(!status.running)
        #expect(status.events.map(\.kind) == ["thinking", "tool_started"])
        let answer = try #require(status.reply)
        #expect(answer.check.clean)
        #expect(answer.withheldResults == 2)
        #expect(answer.omittedMessages == 3)
        let olderJson = json.replacingOccurrences(of: ",\"omittedMessages\":3", with: "")
        let older = try JSONDecoder().decode(WorkerReply<AgentRunStatus>.self, from: Data(olderJson.utf8))
        #expect(older.data?.reply?.omittedMessages == nil)
        #expect(answer.citations.first?.sourceName == "download.qfx")
    }

    @Test func persistedConversationIncludesPinnedModelAndEffort() throws {
        let json = """
            {"conversationId":"c1","backendId":"codex","title":"Chat","createdUtc":"2026-11-14T12:00:00Z",
             "updatedUtc":"2026-11-14T12:00:00Z","model":"gpt-6","effort":"high","contextPreset":"compact"}
            """
        let conversation = try JSONDecoder().decode(AgentConversation.self, from: Data(json.utf8))
        #expect(conversation.model == "gpt-6")
        #expect(conversation.effort == "high")
        #expect(conversation.contextPreset == "compact")
        let legacy = """
            {"conversationId":"c2","backendId":"ollama","title":"Old","createdUtc":"","updatedUtc":""}
            """
        let older = try JSONDecoder().decode(AgentConversation.self, from: Data(legacy.utf8))
        #expect(older.model == nil)
        #expect(older.effort == nil)
    }

    @Test func reopenedChatOnlyRendersCitedTimedEvidence() throws {
        let json = """
            {"role":"assistant","content":"Here is the relevant clip [2].","toolCalls":[],"toolCallId":null,
             "citations":[{"sourceId":"s","itemId":"i","revision":2,"locator":"time:12-18","sourceName":"unused.mp4"},
                          {"sourceId":"t","itemId":"j","revision":3,"locator":"time:2.95-9","sourceName":"relevant.mp4","playbackTimebase":"media-clock-v1"}]}
            """
        let message = try JSONDecoder().decode(AgentMessage.self, from: Data(json.utf8))
        let line = try #require(ChatLine.lines([message]).first)
        let clips = InlineChatClips.cited(in: line.text, citations: line.citations)
        #expect(clips.count == 1)
        #expect(clips.first?.number == 2)
        #expect(clips.first?.range.start == 2.95)
        #expect(clips.first?.citation.revision == 3)
        #expect(clips.first?.citation.playbackTimebase == "media-clock-v1")
        #expect(MediaTimeRange(locator: "time:0-inf") == nil)
    }

    @Test func policyDecodesWithRulesFallbacksAndPendingChanges() throws {
        let json = """
            {"ok":true,"data":{"policy":{"mode":"standing_grants","toolPermission":"read_only","holdDuration":"3.00:00:00",
               "rules":[{"ruleId":"r","category":"financial","providerId":"claude-code","purpose":"intake","allow":true,"expiresUtc":null}],
               "fallbacks":{"financial":["hold","index_only"]}},
             "tiers":[{"tier":"strong","providerId":"claude-code"}],
             "pending":[{"changeId":"p","change":{"mode":"allow_all","addRule":null,"removeRuleId":null,"fallbackCategory":null,
               "fallbackChain":null,"toolPermission":null},"requestedBy":"agent","rationale":"asked","requestedUtc":"2026-11-13T12:00:00Z"}]}}
            """
        let data = try #require(try JSONDecoder().decode(WorkerReply<PolicyData>.self, from: Data(json.utf8)).data)
        #expect(data.policy.fallbacks(for: "financial") == ["hold", "index_only"])
        #expect(data.policy.fallbacks(for: "health") == DisclosurePolicy.defaultFallbacks)
        #expect(PolicyLabels.change(data.pending[0].change).contains("Allow all"))
        #expect(data.tiers.first?.providerId == "claude-code")
    }

    @Test func approvalDescribesTheExactDisclosureRuleAndFallbackOrder() throws {
        let grant = """
            {"addRule":{"ruleId":"r","category":"health","providerId":"claude-code","purpose":"chat",
             "allow":true,"expiresUtc":"2026-11-20T00:00:00Z"}}
            """
        let change = try JSONDecoder().decode(PolicyChangeSummary.self, from: Data(grant.utf8))
        let description = PolicyLabels.change(change)
        #expect(description.contains("Allow Health → claude-code for chat"))
        #expect(description.contains("expires 2026-11-20T00:00:00Z"))
        let removal = try JSONDecoder().decode(PolicyChangeSummary.self, from: Data("{\"removeRuleId\":\"r\"}".utf8))
        #expect(PolicyLabels.change(removal, rules: [try #require(change.addRule)]) == "Remove rule: \(description)")
        let order = Data("{\"fallbackCategory\":\"health\",\"fallbackChain\":[\"hold\",\"skip\"]}".utf8)
        let fallback = try JSONDecoder().decode(PolicyChangeSummary.self, from: order)
        #expect(PolicyLabels.change(fallback).contains("Hold for my decision → Skip"))
    }

    @Test func transcriptHidesRepairPromptsAndSummarizesToolTraffic() {
        let messages = [
            AgentMessage(role: "user", content: "What did I spend?", toolCalls: [], toolCallId: nil),
            AgentMessage(role: "assistant", content: "", toolCalls: [AgentToolCall(callId: "1", name: "data_query", argumentsJson: "{}")], toolCallId: nil),
            AgentMessage(role: "tool", content: "{\"result\":{}}", toolCalls: [], toolCallId: "1"),
            AgentMessage(role: "assistant", content: "You spent $9.", toolCalls: [], toolCallId: nil),
            AgentMessage(role: "user", content: "Answer check failed. These figures…", toolCalls: [], toolCallId: nil),
            AgentMessage(role: "assistant", content: "You spent $8 [1].", toolCalls: [], toolCallId: nil)
        ]
        let lines = ChatLine.lines(messages)
        #expect(lines.map(\.kind) == [.owner, .activity, .agent, .agent])
        #expect(lines[1].text == "Used exact data query")
        #expect(!lines.contains { $0.text.hasPrefix("Answer check failed") })
    }

    @Test func exhaustedToolLoopShowsItsFinalNoticeAfterActivity() {
        let messages = [
            AgentMessage(role: "user", content: "Find my lease", toolCalls: [], toolCallId: nil),
            AgentMessage(role: "assistant", content: "",
                         toolCalls: [AgentToolCall(callId: "1", name: "knowledge_search", argumentsJson: "{}")], toolCallId: nil),
            AgentMessage(role: "tool", content: "No further evidence", toolCalls: [], toolCallId: "1"),
            AgentMessage(role: "assistant", content: "I stopped because this request reached its step limit. Ask me to continue.",
                         toolCalls: [], toolCallId: nil)
        ]
        let lines = ChatLine.lines(messages)
        #expect(lines.map(\.kind) == [.owner, .activity, .agent])
        #expect(lines.last?.text == messages.last?.content)
    }

    @Test func progressAndAnswerCheckWordingIsPlain() {
        let event = AgentRunEvent(sequence: 3, kind: "tool_started", tool: "knowledge_search", detail: nil, atUtc: "")
        #expect(ChatActivity.progress(event) == "Using search…")
        #expect(ChatActivity.progress(nil) == "Starting…")
        let check = AnswerCheck(unsupportedFigures: ["$180.00"], missingCitations: true, repaired: true)
        #expect(!check.clean)
        #expect(AnswerCheckText.describe(check).hasPrefix("Still flagged after one correction"))
        #expect(AnswerCheckText.describe(check).contains("$180.00"))
    }

    @Test(arguments: [
        (["local_if_verified", "hold", "index_only"], "hold", false, ["local_if_verified", "index_only"]),
        (["hold", "index_only"], "index_only", false, ["hold", "index_only"]),
        (["index_only"], "skip", true, ["index_only", "skip"]),
        (["hold", "index_only"], "local_if_verified", true, ["local_if_verified", "hold", "index_only"])
    ])
    func fallbackChainsStayOrderedAndTerminated(chain: [String], step: String, enabled: Bool, expected: [String]) {
        #expect(FallbackChain.toggled(chain, step: step, enabled: enabled) == expected)
    }

    @MainActor
    @Test func graphInvestigationPreservesUnsentChatAndDoesNotStartAModel() {
        let chat = ChatWorkbenchModel(channel: WorkerChannel(worker: WorkerSession()))
        chat.draft = "Unsent owner notes"
        #expect(!chat.prepareDraft("Explain the graph"))
        #expect(chat.draft == "Unsent owner notes")
        chat.draft = ""
        #expect(chat.prepareDraft("Explain the graph"))
        #expect(chat.draft == "Explain the graph")
        #expect(!chat.running)
        #expect(!chat.canSend)
    }

    @Test func chatFailureLabelsNeverRevealProviderErrorText() {
        #expect(ChatFailureText.describe("model_timeout").contains("timed out"))
        #expect(ChatFailureText.describe("model_unavailable").contains("unreachable"))
        #expect(ChatFailureText.describe("model_unauthenticated").contains("sign-in"))
        #expect(ChatFailureText.describe("model_quota_exhausted").contains("usage limit"))
        #expect(ChatFailureText.describe("agent_internal_error").contains("unexpectedly"))
        #expect(!ChatFailureText.describe("exception: secret-key").contains("secret-key"))
    }

    @Test func backendLocalityMirrorsTheWorker() {
        func profile(_ kind: String, _ endpoint: String?) -> AgentBackendProfile {
            AgentBackendProfile(backendId: "b", kind: kind, endpoint: endpoint, model: "m", keyAccount: nil, executable: nil, enabled: true)
        }
        #expect(profile("openai_compatible", "http://127.0.0.1:11434/v1").isLocal)
        #expect(!profile("openai_compatible", "https://api.openai.com/v1").isLocal)
        #expect(!profile("claude_code", nil).isLocal)
        #expect(!profile("anthropic", "https://api.anthropic.com").isLocal)
    }

    @MainActor
    @Test func ollamaChatPickerOmitsEmbeddingOnlyModelsEvenWithCustomNames() throws {
        let json = """
            [{"name":"renamed:latest","digest":"a","sizeBytes":8,"capabilities":{"chat":false,"tools":true,"embedding":true}},
             {"name":"gemma3:latest","digest":"b","sizeBytes":8,"capabilities":{"chat":true,"tools":false,"embedding":false}},
             {"name":"older-embedding:latest","digest":"c","sizeBytes":8}]
            """
        let installed = try JSONDecoder().decode([InstalledModel].self, from: Data(json.utf8))
        let settings = ModelSetupModel(channel: WorkerChannel(worker: WorkerSession()))
        #expect(settings.models(for: ModelTool.ollama, installed: installed).map(\.id) == ["gemma3:latest"])
        #expect(InstalledModel.matching("gemma3", in: installed)?.capabilities?.tools == false)
    }

    @Test func keychainAccountsAreBounded() {
        #expect(KeychainStore.valid("anthropic"))
        #expect(KeychainStore.valid("openai.work@home"))
        #expect(!KeychainStore.valid(""))
        #expect(!KeychainStore.valid("has space"))
        #expect(!KeychainStore.valid(String(repeating: "a", count: 129)))
    }

    @Test func proposalsAndHoldNoticesReadNaturally() {
        let proposal = AgentProposal(proposalId: "p", kind: "source_category", payloadJson: "{\"sourceId\":\"s\",\"category\":\"personal\"}",
                                     rationale: "lease", status: "pending", createdUtc: "")
        #expect(ProposalText.title(proposal) == "Mark a source as Personal")
        let hold = DisclosureHold(holdId: "h", sourceId: "s", sourceName: "card.pdf", itemId: "i", revision: 1,
                                  processorKey: "financial-records-v1", category: "financial", providerId: "claude-code",
                                  purpose: "intake", status: "open", deadlineUtc: "2026-11-16T12:00:00Z", createdUtc: "")
        #expect(ApprovalsWorkbenchModel.holdNotice("grant_category", hold) == "Financial files may now go to claude-code for intake.")
        #expect(PolicyLabels.step(hold.processorKey) == "read the document")
    }
}
