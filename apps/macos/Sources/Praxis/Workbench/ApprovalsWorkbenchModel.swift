import Combine
import Foundation

/// Everything waiting for the owner: disclosure holds from intake, permission widenings the agent
/// requested, the agent's knowledge proposals, and memories it wants to keep. Decisions made here
/// come from the owner's own app over the private pipe; nothing a model emits can make them.
@MainActor
final class ApprovalsWorkbenchModel: ObservableObject {
    static let holdDecisions = ["approve_once", "grant_category", "keep_local", "skip"]

    private let channel: WorkerChannel

    @Published private(set) var holds: [DisclosureHold] = []
    @Published private(set) var actions: [CapabilityItem] = []
    @Published private(set) var decidingActions: Set<String> = []
    @Published private(set) var pending: [PendingPolicyChange] = []
    @Published private(set) var proposals: [AgentProposal] = []
    @Published private(set) var memories: [AgentMemory] = []
    @Published private(set) var error: String? { didSet { if error != nil { notice = nil } } }
    @Published private(set) var notice: String? { didSet { if notice != nil { error = nil } } }

    init(channel: WorkerChannel) {
        self.channel = channel
    }

    var actionRequests: [CapabilityItem] { actions.filter { $0.status == "pending" } }
    var actionHistory: [CapabilityItem] { Array(actions.filter { $0.status != "pending" }.prefix(10)) }
    var count: Int { holds.count + pending.count + proposals.count + memories.count + actionRequests.count }

    func refresh() {
        channel.send("capabilityRequests", as: CapabilityRequestsData.self, failed: report) { [weak self] data in
            self?.actions = data.requests
        }
        channel.send("disclosureHolds", ["limit": 200], as: DisclosureHoldsData.self, failed: report) { [weak self] data in
            self?.holds = data.holds
        }
        channel.send("policyGet", as: PolicyData.self, failed: report) { [weak self] data in
            self?.pending = data.pending
        }
        channel.send("agentProposals", ["status": "pending", "limit": 200], as: AgentProposalsData.self, failed: report) { [weak self] data in
            self?.proposals = data.proposals
        }
        channel.send("agentMemories", ["status": "proposed", "limit": 200], as: AgentMemoriesData.self, failed: report) { [weak self] data in
            self?.memories = data.memories
        }
    }

    func decide(_ action: CapabilityItem, accept: Bool) {
        guard decidingActions.insert(action.id).inserted else { return }
        channel.send("capabilityDecide", ["id": action.id, "accept": accept], as: CapabilityItem.self, failed: { [weak self] error in
            self?.decidingActions.remove(action.id)
            self?.report(error)
            self?.refresh()
        }) { [weak self] item in
            self?.decidingActions.remove(action.id)
            self?.notice = item.status == "accepted" ? "Action completed." : item.status == "rejected" ? "Action rejected." : nil
            self?.error = item.status == "failed" ? "Action failed. See the action history for details." : nil
            self?.refresh()
        }
    }

    func decide(_ hold: DisclosureHold, _ decision: String) {
        guard Self.holdDecisions.contains(decision) else { return }
        channel.send("disclosureHoldDecide", ["holdId": hold.holdId, "decision": decision], as: ChangedFlag.self, failed: report) { [weak self] data in
            self?.notice = data.changed ? Self.holdNotice(decision, hold) : "That item was already decided."
            self?.refresh()
        }
    }

    func resolve(_ change: PendingPolicyChange, confirm: Bool) {
        channel.send("policyResolve", ["changeId": change.changeId, "confirm": confirm], as: ChangedFlag.self, failed: report) { [weak self] data in
            self?.notice = data.changed ? (confirm ? "Change applied." : "Change rejected.") : "That change was already resolved."
            self?.refresh()
        }
    }

    func resolve(_ proposal: AgentProposal, accept: Bool) {
        channel.send("agentProposalResolve", ["proposalId": proposal.proposalId, "accept": accept], as: ChangedFlag.self, failed: report) { [weak self] data in
            self?.notice = data.changed ? (accept ? "Suggestion applied." : "Suggestion rejected.") : "The suggestion could not be applied."
            self?.refresh()
        }
    }

    func resolve(_ memory: AgentMemory, accept: Bool) {
        channel.send("agentMemoryResolve", ["memoryId": memory.memoryId, "accept": accept], as: ChangedFlag.self, failed: report) { [weak self] _ in
            self?.notice = accept ? "Praxis will remember that." : "Discarded."
            self?.refresh()
        }
    }

    nonisolated static func holdNotice(_ decision: String, _ hold: DisclosureHold) -> String {
        switch decision {
        case "approve_once": "\(hold.sourceName) will be sent to \(hold.providerId) once."
        case "grant_category": "\(PolicyLabels.category(hold.category)) files may now go to \(hold.providerId) for intake."
        case "keep_local": "\(hold.sourceName) stays on this Mac."
        default: "\(hold.sourceName) will be skipped."
        }
    }

    private func report(_ message: String) { error = message }
}
