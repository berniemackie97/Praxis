import SwiftUI

struct TopicReviewView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @State private var correctedTopic = ""

    var body: some View {
        DisclosureGroup("Suggested topics (\(model.topicProposals.count))") {
            VStack(alignment: .leading, spacing: 8) {
                Text("Candidates come from literal Markdown headings, not a model. Reviewing them does not rewrite a source or assign a collection.")
                    .font(.caption).foregroundStyle(.secondary)
                Text("Processing: \(model.topicOutcome)")
                    .font(.caption).foregroundStyle(.secondary)
                if model.topicProposals.isEmpty {
                    Text("No reviewable heading candidates yet. Praxis may be waiting for extraction, abstaining, or handling an unsupported source.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                ScrollView {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(model.topicProposals) { proposal in
                            VStack(alignment: .leading, spacing: 3) {
                                Text(proposal.value).fontWeight(.medium)
                                Text("Evidence: \(proposal.evidence) · \(proposal.producer) · \(proposal.status)")
                                    .font(.caption).foregroundStyle(.secondary)
                                if let correction = proposal.correction {
                                    Text("Owner correction: \(correction)").font(.caption)
                                }
                                HStack {
                                    Button("Accept") {
                                        model.reviewTopic(proposal, decision: "accepted")
                                    }
                                    Button("Reject") {
                                        model.reviewTopic(proposal, decision: "rejected")
                                    }
                                }
                            }
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 165)
                if !model.topicProposals.isEmpty {
                    HStack {
                        TextField("Correct topic text", text: $correctedTopic)
                            .textFieldStyle(.roundedBorder)
                        Menu("Correct…") {
                            ForEach(model.topicProposals) { proposal in
                                Button(proposal.value) {
                                    model.reviewTopic(proposal, decision: "corrected",
                                        correction: correctedTopic)
                                }
                            }
                        }
                        .disabled(correctedTopic.trimmingCharacters(
                            in: .whitespacesAndNewlines).isEmpty ||
                            correctedTopic.count > 80)
                    }
                }
            }
        }
    }
}
