import SwiftUI

/// The owner's decision queue. Holds expire into their fallback automatically; nothing waits forever.
struct ApprovalsView: View {
    @ObservedObject var model: ApprovalsWorkbenchModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                HStack {
                    Text("Waiting for you").font(.headline)
                    Spacer()
                    Button("Refresh") { model.refresh() }
                }
                if model.count == 0 {
                    Text("Nothing needs a decision.").foregroundStyle(.secondary)
                }
                if !model.holds.isEmpty {
                    Section(title: "Send to a stronger model?",
                            caption: "Intake needs a remote model your policy has not granted. If you do nothing, each item falls back when its deadline passes.") {
                        ForEach(model.holds) { hold in HoldRow(hold: hold, model: model) }
                    }
                }
                if !model.pending.isEmpty {
                    Section(title: "Permission changes requested in chat",
                            caption: "The agent asked to widen what may leave this Mac or what it may change. Only you can confirm.") {
                        ForEach(model.pending) { change in
                            DecisionRow(title: PolicyLabels.change(change.change),
                                        detail: change.rationale.isEmpty ? "Requested by \(change.requestedBy)" : "“\(change.rationale)”",
                                        accept: "Confirm", reject: "Reject") { model.resolve(change, confirm: $0) }
                        }
                    }
                }
                if !model.proposals.isEmpty {
                    Section(title: "Knowledge suggestions", caption: "Corrections the agent proposed from evidence.") {
                        ForEach(model.proposals) { proposal in
                            DecisionRow(title: ProposalText.title(proposal), detail: proposal.rationale,
                                        accept: "Apply", reject: "Reject") { model.resolve(proposal, accept: $0) }
                        }
                    }
                }
                if !model.memories.isEmpty {
                    Section(title: "Things to remember", caption: "Preferences the agent heard in conversation.") {
                        ForEach(model.memories) { memory in
                            DecisionRow(title: memory.text, detail: nil, accept: "Remember", reject: "Discard") { model.resolve(memory, accept: $0) }
                        }
                    }
                }
                FeedbackLine(error: model.error, notice: model.notice)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .onAppear { model.refresh() }
    }
}

private struct Section<Content: View>: View {
    let title: String
    let caption: String
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.subheadline.weight(.semibold))
            Text(caption).font(.caption).foregroundStyle(.secondary)
            content
        }
    }
}

private struct HoldRow: View {
    let hold: DisclosureHold
    @ObservedObject var model: ApprovalsWorkbenchModel

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("\(hold.sourceName) → \(hold.providerId)").fontWeight(.medium)
            Text("\(PolicyLabels.category(hold.category)) · to \(PolicyLabels.step(hold.processorKey)) · decide by \(DateText.short(hold.deadlineUtc))")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                Button("Send once") { model.decide(hold, "approve_once") }
                Button("Always allow \(PolicyLabels.category(hold.category))") { model.decide(hold, "grant_category") }
                Button("Keep local") { model.decide(hold, "keep_local") }
                Button("Skip") { model.decide(hold, "skip") }
            }
            .controlSize(.small)
        }
        .padding(10)
        .background(Color.secondary.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
    }
}

private struct DecisionRow: View {
    let title: String
    let detail: String?
    let accept: String
    let reject: String
    let decide: (Bool) -> Void

    var body: some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 3) {
                Text(title).fontWeight(.medium).textSelection(.enabled)
                if let detail { Text(detail).font(.caption).foregroundStyle(.secondary).textSelection(.enabled) }
            }
            Spacer()
            Button(reject) { decide(false) }
            Button(accept) { decide(true) }.buttonStyle(.borderedProminent)
        }
        .controlSize(.small)
        .padding(10)
        .background(Color.secondary.opacity(0.06), in: RoundedRectangle(cornerRadius: 8))
    }
}

/// Short descriptions of agent proposals.
enum ProposalText {
    static func title(_ proposal: AgentProposal) -> String {
        let payload = (try? JSONSerialization.jsonObject(with: Data(proposal.payloadJson.utf8))) as? [String: Any] ?? [:]
        switch proposal.kind {
        case "entity_type": return "Set entity type to \(payload["entityType"] as? String ?? "?")"
        case "entity_rename": return "Rename entity to “\(payload["name"] as? String ?? "?")”"
        case "entity_merge": return "Merge two entities that are the same"
        case "relation": return "Relate entities: \(payload["predicate"] as? String ?? "?")"
        case "source_category": return "Mark a source as \(PolicyLabels.category(payload["category"] as? String ?? "?"))"
        default: return proposal.kind
        }
    }
}

/// Compact dates for timestamps the worker sends in ISO 8601.
enum DateText {
    static func short(_ iso: String) -> String {
        let parser = ISO8601DateFormatter()
        parser.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        guard let date = parser.date(from: iso) ?? ISO8601DateFormatter().date(from: iso) else { return iso }
        return date.formatted(date: .abbreviated, time: .shortened)
    }
}
