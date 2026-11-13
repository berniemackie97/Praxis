import SwiftUI

/// Suggestions for the selected source: heading topics and the local model's topics, entities
/// and relations, each with the exact text it came from. Reviewing never edits the source.
struct ProposalReviewView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @State private var correcting: TopicProposal?
    @State private var correction = ""

    private var pending: Int { model.topicProposals.filter { $0.status == "pending" }.count }

    var body: some View {
        Panel(title: "Suggestions", systemImage: "lightbulb",
              caption: "Headings: \(model.topicOutcome) · Local model: \(model.semanticOutcome)") {
            if let selected = model.selected,
               model.topicProposals.contains(where: { $0.kind != "topic" && $0.status == "pending" }) {
                Button("Ask a Stronger Model…") { model.verification.requestForSource(selected.sourceId) }
                    .controlSize(.small)
                    .help("Creates a verification request for review under Verification. Nothing is sent until you approve it.")
            }
        } content: {
            if model.topicProposals.isEmpty {
                Text("No suggestions yet. Praxis may still be extracting, or found nothing to suggest.")
                    .font(.callout).foregroundStyle(.secondary)
            } else {
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(Array(model.topicProposals.enumerated()), id: \.element.id) { index, proposal in
                        if index > 0 { Divider().padding(.vertical, Spacing.s) }
                        ProposalRow(proposal: proposal,
                                    review: { model.reviewTopic(proposal, decision: $0) },
                                    correct: proposal.kind == "relation" ? nil : {
                                        correction = proposal.value
                                        correcting = proposal
                                    })
                    }
                }
            }
        }
        .sheet(item: $correcting) { proposal in
            CorrectionSheet(proposal: proposal, correction: $correction) {
                model.reviewTopic(proposal, decision: "corrected", correction: correction)
                correcting = nil
            } cancel: { correcting = nil }
        }
    }
}

private struct ProposalRow: View {
    let proposal: TopicProposal
    let review: (String) -> Void
    let correct: (() -> Void)?

    var body: some View {
        HStack(alignment: .top, spacing: Spacing.m) {
            VStack(alignment: .leading, spacing: Spacing.xs) {
                HStack(spacing: Spacing.s) {
                    Text(proposal.title).font(.rowTitle)
                    if proposal.instructionLike {
                        StatusPill(text: "Reads like an instruction", tone: .warning, systemImage: "exclamationmark.triangle")
                            .help("Hidden from answers until a stronger model checks it.")
                    }
                }
                Text("“\(proposal.evidence)”").font(.callout).foregroundStyle(.secondary).textSelection(.enabled).lineLimit(3)
                Text("\(proposal.fromModel ? "Local model" : "Markdown heading")" +
                     (proposal.correction.map { " · corrected to \($0)" } ?? ""))
                    .font(.caption2).foregroundStyle(.tertiary)
            }
            Spacer(minLength: Spacing.s)
            if proposal.status == "pending" {
                HStack(spacing: Spacing.xs) {
                    if let correct {
                        Button(action: correct) { Image(systemName: "pencil") }
                            .help("Correct…").accessibilityLabel("Correct")
                    }
                    Button { review("rejected") } label: { Image(systemName: "xmark") }
                        .help("Reject").accessibilityLabel("Reject")
                    Button { review("accepted") } label: { Image(systemName: "checkmark") }
                        .help("Accept").accessibilityLabel("Accept")
                        .buttonStyle(.borderedProminent)
                }
                .controlSize(.small)
            } else {
                StatusPill(text: proposal.status.capitalized, tone: proposal.status == "accepted" || proposal.status == "corrected" ? .positive : .neutral)
            }
        }
    }
}

private struct CorrectionSheet: View {
    let proposal: TopicProposal
    @Binding var correction: String
    let save: () -> Void
    let cancel: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.l) {
            Text("Correct suggestion").font(.headline)
            Text("“\(proposal.evidence)”").font(.callout).foregroundStyle(.secondary).lineLimit(4)
            TextField("Corrected name", text: $correction).textFieldStyle(.roundedBorder).onSubmit(save)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel, action: cancel).keyboardShortcut(.cancelAction)
                Button("Save Correction", action: save)
                    .keyboardShortcut(.defaultAction)
                    .disabled(correction.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || correction.count > 120)
            }
        }
        .padding(Spacing.xl)
        .frame(width: 420)
    }
}
