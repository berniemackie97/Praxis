import SwiftUI

/// The owner's decision queue. Holds expire into their fallback automatically; nothing waits forever.
struct ApprovalsView: View {
    @ObservedObject var model: ApprovalsWorkbenchModel

    var body: some View {
        Group {
            if model.count == 0 && model.actionHistory.isEmpty {
                EmptyState(systemImage: "checkmark.seal",
                           title: "You're all caught up",
                           message: "Requests to send data to a stronger model, permission changes from chat, and the agent's suggestions appear here.")
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: Spacing.xl) {
                        FeedbackLine(error: model.error, notice: model.notice)
                        if !model.actionRequests.isEmpty {
                            QueueSection(title: "Actions requested in Chat", systemImage: "terminal",
                                         caption: "Check the exact command or address. Approval runs the action once.",
                                         count: model.actionRequests.count) {
                                ForEach(model.actionRequests) { item in
                                    DecisionCard(systemImage: "terminal", title: item.title,
                                                 detail: [item.description, item.target].compactMap { $0 }.joined(separator: "\n"),
                                                 accept: "Approve and Run", reject: "Reject") { model.decide(item, accept: $0) }
                                        .disabled(model.decidingActions.contains(item.id))
                                }
                            }
                        }
                        if !model.actionHistory.isEmpty {
                            DisclosureGroup("Recent Chat actions") {
                                ForEach(model.actionHistory) { item in
                                    VStack(alignment: .leading, spacing: Spacing.xs) {
                                        Text("\(item.title) · \(item.status.capitalized)").font(.rowTitle)
                                        if let target = item.target { Text(target).textSelection(.enabled) }
                                        if let detail = item.detail {
                                            Text(detail).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                                        }
                                    }
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .padding(.vertical, Spacing.s)
                                }
                            }
                        }
                        if !model.holds.isEmpty {
                            QueueSection(title: "Send to a stronger model?", systemImage: "arrow.up.forward.app",
                                         caption: "Intake needs a remote model your policy hasn't granted. If you do nothing, each item continues down its fallback chain when the deadline passes.",
                                         count: model.holds.count) {
                                ForEach(model.holds) { hold in HoldCard(hold: hold, model: model) }
                            }
                        }
                        if !model.pending.isEmpty {
                            QueueSection(title: "Permission changes requested in chat", systemImage: "lock.open",
                                         caption: "Review the exact change. Nothing takes effect until you confirm.",
                                         count: model.pending.count) {
                                ForEach(model.pending) { change in
                                    DecisionCard(systemImage: "hand.raised", title: PolicyLabels.change(change.change, rules: model.policyRules),
                                                 detail: change.rationale.isEmpty ? "Requested by \(change.requestedBy)" : "“\(change.rationale)”",
                                                 accept: "Confirm", reject: "Reject") { model.resolve(change, confirm: $0) }
                                }
                            }
                        }
                        if !model.proposals.isEmpty {
                            QueueSection(title: "Knowledge suggestions", systemImage: "lightbulb",
                                         caption: "Corrections the agent proposed from evidence.", count: model.proposals.count) {
                                ForEach(model.proposals) { proposal in
                                    DecisionCard(systemImage: "point.3.connected.trianglepath.dotted", title: ProposalText.title(proposal),
                                                 detail: proposal.rationale, accept: "Apply", reject: "Reject") { model.resolve(proposal, accept: $0) }
                                }
                            }
                        }
                        if !model.memories.isEmpty {
                            QueueSection(title: "Things to remember", systemImage: "brain",
                                         caption: "Facts, preferences, and corrections to review.", count: model.memories.count) {
                                ForEach(model.memories) { memory in
                                    DecisionCard(systemImage: "text.quote", title: memory.text, detail: memory.ownerStatement,
                                                 accept: "Remember", reject: "Discard") { model.resolve(memory, accept: $0) }
                                }
                            }
                        }
                    }
                    .frame(maxWidth: 820, alignment: .leading)
                    .padding(Spacing.xl)
                    .frame(maxWidth: .infinity)
                }
            }
        }
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button { model.refresh() } label: { Label("Refresh", systemImage: "arrow.clockwise") }
            }
        }
        .onAppear { model.refresh() }
    }
}

private struct QueueSection<Content: View>: View {
    let title: String
    let systemImage: String
    let caption: String
    let count: Int
    @ViewBuilder let content: Content

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
                Label(title, systemImage: systemImage).font(.sectionTitle)
                Text("\(count)").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    .padding(.horizontal, 6).padding(.vertical, 1)
                    .background(Surface.well, in: Capsule())
            }
            Text(caption).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            VStack(spacing: Spacing.s) { content }
        }
    }
}

private struct HoldCard: View {
    let hold: DisclosureHold
    @ObservedObject var model: ApprovalsWorkbenchModel

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.m) {
            HStack(alignment: .top, spacing: Spacing.m) {
                Image(systemName: SourceSymbol.of(hold.sourceName))
                    .font(.system(size: 18)).foregroundStyle(.secondary).frame(width: 24)
                VStack(alignment: .leading, spacing: Spacing.xxs) {
                    Text(hold.sourceName).font(.rowTitle).textSelection(.enabled)
                    Text("\(PolicyLabels.category(hold.category)) · to \(hold.providerId) for \(PolicyLabels.step(hold.processorKey))")
                        .font(.meta).foregroundStyle(.secondary)
                }
                Spacer()
                StatusPill(text: "Falls back \(DateText.relative(hold.deadlineUtc))", tone: .warning, systemImage: "clock")
                    .help("Decide by \(DateText.short(hold.deadlineUtc))")
            }
            HStack(spacing: Spacing.s) {
                Button("Keep Local") { model.decide(hold, "keep_local") }
                Menu("More") {
                    Button("Skip This File") { model.decide(hold, "skip") }
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
                Spacer()
                Button("Always Allow \(PolicyLabels.category(hold.category))") { model.decide(hold, "grant_category") }
                    .help("Grants \(PolicyLabels.category(hold.category)) to \(hold.providerId) for intake from now on.")
                Button("Send Once") { model.decide(hold, "approve_once") }
                    .buttonStyle(.borderedProminent)
            }
            .controlSize(.regular)
        }
        .padding(Spacing.l)
        .background(Surface.card, in: RoundedRectangle(cornerRadius: Radius.l, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: Radius.l, style: .continuous).strokeBorder(Surface.stroke.opacity(0.6)))
    }
}

private struct DecisionCard: View {
    let systemImage: String
    let title: String
    let detail: String?
    let accept: String
    let reject: String
    let decide: (Bool) -> Void

    var body: some View {
        HStack(alignment: .center, spacing: Spacing.m) {
            Image(systemName: systemImage).font(.system(size: 16)).foregroundStyle(.secondary).frame(width: 24)
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text(title).font(.rowTitle).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                if let detail, !detail.isEmpty {
                    Text(detail).font(.meta).foregroundStyle(.secondary).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: Spacing.l)
            Button(reject) { decide(false) }
            Button(accept) { decide(true) }.buttonStyle(.borderedProminent)
        }
        .padding(Spacing.l)
        .background(Surface.card, in: RoundedRectangle(cornerRadius: Radius.l, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: Radius.l, style: .continuous).strokeBorder(Surface.stroke.opacity(0.6)))
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
