import SwiftUI

/// What intake found in the selected source. Verified knowledge needs no review; each item can be
/// renamed or removed. Unconfirmed guesses from the local model wait in a collapsed list that can
/// be settled in one click. Nothing here ever edits the source.
struct ProposalReviewView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @State private var correcting: TopicProposal?
    @State private var correction = ""
    @State private var showSuggestions = false

    private var confirmed: [TopicProposal] { model.topicProposals.filter(\.isConfirmed) }
    private var suggestions: [TopicProposal] { model.topicProposals.filter { !$0.isConfirmed && $0.status == "pending" } }

    var body: some View {
        Panel(title: "Found in this file", systemImage: "sparkle.magnifyingglass", caption: caption) {
            EmptyView()
        } content: {
            VStack(alignment: .leading, spacing: Spacing.m) {
                if confirmed.isEmpty && suggestions.isEmpty {
                    Text(emptyText).font(.callout).foregroundStyle(.secondary)
                }
                ForEach(groups, id: \.title) { group in
                    VStack(alignment: .leading, spacing: Spacing.xs) {
                        Text(group.title).font(.meta).foregroundStyle(.secondary)
                        FlowLayout(spacing: Spacing.xs) {
                            ForEach(group.items) { item in
                                FoundChip(proposal: item,
                                          remove: { model.reviewTopic(item, decision: "rejected") },
                                          rename: item.kind == "relation" ? nil : {
                                              correction = item.correction ?? item.value
                                              correcting = item
                                          })
                            }
                        }
                    }
                }
                if !suggestions.isEmpty {
                    Divider()
                    DisclosureGroup(isExpanded: $showSuggestions) {
                        VStack(alignment: .leading, spacing: Spacing.s) {
                            ForEach(suggestions) { proposal in
                                SuggestionRow(proposal: proposal, review: { model.reviewTopic(proposal, decision: $0) })
                            }
                        }
                        .padding(.top, Spacing.s)
                    } label: {
                        HStack {
                            Text("\(Count.label(suggestions.count, "unconfirmed guess")) from the local model")
                                .font(.callout).foregroundStyle(.secondary)
                            Spacer()
                            Button("Add All") { model.reviewAllSuggestions("accepted") }.controlSize(.small)
                            Button("Dismiss All") { model.reviewAllSuggestions("rejected") }.controlSize(.small)
                            if let selected = model.selected {
                                Button("Check with a Stronger Model…") { model.verification.requestForSource(selected.sourceId) }
                                    .controlSize(.small)
                                    .help("Creates a verification request under Verification. Nothing is sent until you approve it.")
                            }
                        }
                    }
                }
            }
        }
        .sheet(item: $correcting) { proposal in
            RenameSheet(proposal: proposal, correction: $correction) {
                model.reviewTopic(proposal, decision: "corrected", correction: correction)
                correcting = nil
            } cancel: { correcting = nil }
        }
    }

    /// Follows the file's intake state, not the proposal producer, so a file that was only indexed never claims to be "still reading".
    private var caption: String {
        switch model.understanding?.status {
        case "understood", "partially_verified": "Understood and verified automatically"
        case "read", "triaged", nil: "Intake is still reading this file"
        case "held": "Waiting for your approval"
        default: "Not understood yet"
        }
    }

    private var emptyText: String {
        switch model.understanding?.status {
        case "read", "triaged", nil: "Intake is still reading this file."
        case "index_only", "failed", "skipped", "held": "Nothing has been found yet; see Intake above for why."
        default: "No people, organizations, topics or relations were found that could be verified."
        }
    }

    private var groups: [(title: String, items: [TopicProposal])] {
        let people = confirmed.filter { $0.kind == "entity" && $0.entityType == "person" }
        let organizations = confirmed.filter { $0.kind == "entity" && $0.entityType == "organization" }
        let things = confirmed.filter { $0.kind == "entity" && !["person", "organization"].contains($0.entityType ?? "") }
        let topics = confirmed.filter { $0.kind == "topic" }
        let relations = confirmed.filter { $0.kind == "relation" }
        return [("People", people), ("Organizations", organizations), ("Projects, places and things", things),
                ("Topics", topics), ("Relations", relations)].filter { !$0.1.isEmpty }
    }
}

/// One verified item: its name, the quote on hover, and a menu to rename or remove it.
private struct FoundChip: View {
    let proposal: TopicProposal
    let remove: () -> Void
    let rename: (() -> Void)?

    private var label: String {
        switch proposal.kind {
        case "relation": "\(proposal.relationSubject ?? "") \(KnowledgeVocabulary.label(proposal.value)) \(proposal.relationObject ?? "")"
        default: proposal.correction ?? proposal.value
        }
    }

    var body: some View {
        Menu {
            Text("“\(proposal.evidence)”")
            Divider()
            if let rename { Button("Rename…", action: rename) }
            Button("Remove", role: .destructive, action: remove)
        } label: {
            Text(label).font(.callout).lineLimit(1)
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .padding(.horizontal, Spacing.s)
        .padding(.vertical, Spacing.xxs + 1)
        .background(Capsule().fill(Surface.well))
        .help("“\(proposal.evidence)”")
    }
}

/// A local-model guess with its quote, to add or dismiss.
private struct SuggestionRow: View {
    let proposal: TopicProposal
    let review: (String) -> Void

    var body: some View {
        HStack(alignment: .top, spacing: Spacing.m) {
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                HStack(spacing: Spacing.s) {
                    Text(proposal.title).font(.callout)
                    if proposal.instructionLike {
                        StatusPill(text: "Reads like an instruction", tone: .warning, systemImage: "exclamationmark.triangle")
                            .help("Kept out of answers until it is checked.")
                    }
                }
                Text("“\(proposal.evidence)”").font(.meta).foregroundStyle(.secondary).lineLimit(2).textSelection(.enabled)
            }
            Spacer(minLength: Spacing.s)
            Button { review("rejected") } label: { Image(systemName: "xmark") }
                .help("Dismiss").accessibilityLabel("Dismiss")
            Button { review("accepted") } label: { Image(systemName: "plus") }
                .help("Add to knowledge").accessibilityLabel("Add to knowledge")
        }
        .controlSize(.small)
    }
}

private struct RenameSheet: View {
    let proposal: TopicProposal
    @Binding var correction: String
    let save: () -> Void
    let cancel: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.l) {
            Text("Rename").font(.headline)
            Text("“\(proposal.evidence)”").font(.callout).foregroundStyle(.secondary).lineLimit(4)
            TextField("Name", text: $correction).textFieldStyle(.roundedBorder).onSubmit(save)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel, action: cancel).keyboardShortcut(.cancelAction)
                Button("Rename", action: save)
                    .keyboardShortcut(.defaultAction)
                    .disabled(correction.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || correction.count > 120)
            }
        }
        .padding(Spacing.xl)
        .frame(width: 420)
    }
}
