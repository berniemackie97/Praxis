import SwiftUI

/// Suggestions for the selected source: heading topics and the local model's topics, entities
/// and relations, each with the exact text it came from. Reviewing never edits the source.
struct ProposalReviewView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @State private var correction = ""

    private var correctable: [TopicProposal] { model.topicProposals.filter { $0.kind != "relation" } }

    var body: some View {
        DisclosureGroup("Suggestions (\(model.topicProposals.count))") {
            VStack(alignment: .leading, spacing: 8) {
                Text("Headings: \(model.topicOutcome) · Local model: \(model.semanticOutcome)")
                    .font(.caption).foregroundStyle(.secondary)
                if model.topicProposals.isEmpty {
                    Text("No suggestions yet. Praxis may still be extracting, or found nothing to suggest.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                ScrollView {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(model.topicProposals) { proposal in
                            ProposalRow(proposal: proposal) { decision in
                                model.reviewTopic(proposal, decision: decision)
                            }
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 220)
                if !correctable.isEmpty {
                    HStack {
                        TextField("Corrected name", text: $correction).textFieldStyle(.roundedBorder)
                        Menu("Correct…") {
                            ForEach(correctable) { proposal in
                                Button(proposal.title) {
                                    model.reviewTopic(proposal, decision: "corrected", correction: correction)
                                }
                            }
                        }
                        .disabled(correction.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                                  correction.count > 120)
                    }
                }
                if let selected = model.selected,
                   model.topicProposals.contains(where: { $0.kind != "topic" && $0.status == "pending" }) {
                    Button("Ask a stronger model to check these…") {
                        model.verification.requestForSource(selected.sourceId)
                    }
                    .help("Creates a verification request for review under Verification. Nothing is sent until you approve it.")
                }
            }
        }
    }
}

private struct ProposalRow: View {
    let proposal: TopicProposal
    let review: (String) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 6) {
                Text(proposal.title).fontWeight(.medium)
                if proposal.instructionLike {
                    Text("reads like an instruction · hidden until checked")
                        .font(.caption2).foregroundColor(.orange)
                }
            }
            Text("“\(proposal.evidence)”").font(.caption).textSelection(.enabled)
            Text("\(proposal.fromModel ? "local model" : "Markdown heading") · \(proposal.status)" +
                 (proposal.correction.map { " · corrected to \($0)" } ?? ""))
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                Button("Accept") { review("accepted") }
                Button("Reject") { review("rejected") }
            }
            .controlSize(.small)
        }
    }
}
