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
               "inputTokens":900,"outputTokens":40}}}
            """
        let reply = try JSONDecoder().decode(WorkerReply<AgentRunStatus>.self, from: Data(json.utf8))
        let status = try #require(reply.data)
        #expect(!status.running)
        #expect(status.events.map(\.kind) == ["thinking", "tool_started"])
        let answer = try #require(status.reply)
        #expect(answer.check.clean)
        #expect(answer.withheldResults == 2)
        #expect(answer.citations.first?.sourceName == "download.qfx")
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
