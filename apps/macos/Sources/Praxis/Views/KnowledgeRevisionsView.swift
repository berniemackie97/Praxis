import SwiftUI

/// How Praxis revises its own classifications: proposals waiting for you, then what it corrected itself.
struct KnowledgeRevisionsView: View {
    @ObservedObject var model: KnowledgeWorkbenchModel
    let inspect: (String) -> Void

    private var proposals: [KnowledgeRevision] { model.revisions.filter { $0.state == "suggested" } }
    private var history: [KnowledgeRevision] { model.revisions.filter { $0.state != "suggested" } }

    var body: some View {
        List {
            Text("Praxis takes a second look when sources are revoked, new evidence arrives, rules change or you correct something. It fixes its own guesses and asks you before changing anything else.")
                .font(.caption).foregroundStyle(.secondary)
                .listRowSeparator(.hidden)
            Section {
                if proposals.isEmpty {
                    Text("Nothing waiting for review.").foregroundStyle(.secondary)
                }
                ForEach(proposals) { revision in
                    RevisionRow(revision: revision, inspect: inspect) {
                        HStack(spacing: Spacing.s) {
                            Button("Accept") { model.decide(revision, accept: true) }
                                .buttonStyle(.borderedProminent)
                                .accessibilityLabel("Accept for \(revision.entityName): \(RevisionText.change(revision))")
                            Button("Dismiss") { model.decide(revision, accept: false) }
                                .accessibilityLabel("Dismiss proposal for \(revision.entityName)")
                        }
                    }
                }
            } header: {
                Label("Proposed for your review (\(proposals.count))", systemImage: "questionmark.bubble")
            }
            Section {
                ForEach(history) { revision in
                    RevisionRow(revision: revision, inspect: inspect) {
                        Text(RevisionText.state(revision.state)).font(.caption).foregroundStyle(.secondary)
                    }
                }
            } header: {
                Label("History", systemImage: "clock.arrow.circlepath")
            }
        }
        .overlay {
            if model.revisions.isEmpty {
                EmptyState(systemImage: "arrow.triangle.2.circlepath", title: "No revisions yet",
                           message: "Corrections appear here as Praxis re-examines what it knows.")
            }
        }
        .safeAreaInset(edge: .bottom, spacing: 0) {
            if model.error != nil || model.notice != nil {
                FeedbackLine(error: model.error, notice: model.notice).padding(Spacing.s)
            }
        }
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Button { model.refreshRevisions() } label: { Label("Refresh", systemImage: "arrow.clockwise") }
            }
        }
        .onAppear { model.refreshRevisions() }
    }
}

private struct RevisionRow<Actions: View>: View {
    let revision: KnowledgeRevision
    let inspect: (String) -> Void
    @ViewBuilder let actions: () -> Actions

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            HStack(spacing: Spacing.s) {
                Button(revision.entityName) { inspect(revision.entityId) }
                    .buttonStyle(.link).font(.body.weight(.semibold))
                    .help("Open this entity")
                Text(RevisionText.change(revision))
                    .font(.callout.monospaced())
                Spacer()
                Text(RevisionText.trigger(revision.trigger)).font(.caption).foregroundStyle(.secondary)
            }
            Text(revision.basis).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            actions()
        }
        .padding(.vertical, Spacing.xs)
    }
}

enum RevisionText {
    static func trigger(_ trigger: String) -> String {
        switch trigger {
        case "rules": "rules changed"
        case "revocation": "source revoked"
        case "mentions": "new evidence"
        case "owner_lesson": "learned from your fix"
        case "relations": "how it connects"
        case "duplicates": "same name"
        default: trigger
        }
    }

    static func change(_ revision: KnowledgeRevision) -> String {
        switch revision.field {
        case "merge": "merge into \(revision.afterLabel)"
        case "relation": revision.afterLabel
        default: "\(KnowledgeVocabulary.label(revision.before)) → \(KnowledgeVocabulary.label(revision.after))"
        }
    }

    static func accepted(_ revision: KnowledgeRevision) -> String {
        switch revision.field {
        case "merge": "\(revision.entityName) merged into \(revision.afterLabel)."
        case "relation": "Relation retracted."
        default: "\(revision.entityName) is now \(KnowledgeVocabulary.label(revision.after)). Similar entities will be checked."
        }
    }

    static func state(_ state: String) -> String {
        switch state {
        case "applied": "corrected by Praxis"
        case "accepted": "accepted by you"
        case "dismissed": "dismissed"
        default: state
        }
    }
}
