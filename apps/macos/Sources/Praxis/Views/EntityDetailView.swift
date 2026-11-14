import SwiftUI

/// One entity: aliases, evidence, relations, co-mentions and the owner's corrections.
struct EntityDetailView: View {
    let detail: EntityDetail
    @ObservedObject var model: KnowledgeWorkbenchModel
    let verification: VerificationWorkbenchModel
    let exploreConnections: () -> Void
    @State private var renaming = false
    @State private var showAllMentions = false
    @State private var newName = ""
    @State private var predicate = KnowledgeVocabulary.predicates[0]

    private var summary: EntitySummary { detail.summary }
    private var others: [EntitySummary] { model.entities.filter { $0.entityId != summary.entityId } }
    private var relationGroups: [EntityRelationGroup] { EntityRelationGroup.group(detail.relations) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: Spacing.l) {
                header
                if summary.typeConflict {
                    Banner(text: "The local model typed this inconsistently. Set the type yourself, or ask a stronger model to settle it.",
                           tone: .warning, systemImage: "exclamationmark.triangle.fill")
                }
                mentions
                relations
                if !detail.related.isEmpty {
                    Panel(title: "Mentioned together with", systemImage: "link") {
                        FlowLayout(spacing: Spacing.xs) {
                            ForEach(detail.related) { related in
                                Chip(text: "\(related.name)  \(related.sharedSegments)", systemImage: EntitySymbol.of(related.entityType))
                            }
                        }
                    }
                }
            }
            .padding(Spacing.xl)
            .frame(maxWidth: 900, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .onChange(of: summary.entityId) { _ in showAllMentions = false }
        .sheet(isPresented: $renaming) {
            VStack(alignment: .leading, spacing: Spacing.l) {
                Text("Rename \(summary.name)").font(.headline)
                TextField("New name", text: $newName).textFieldStyle(.roundedBorder).onSubmit(rename)
                HStack {
                    Spacer()
                    Button("Cancel", role: .cancel) { renaming = false }.keyboardShortcut(.cancelAction)
                    Button("Rename", action: rename)
                        .keyboardShortcut(.defaultAction)
                        .disabled(newName.trimmingCharacters(in: .whitespaces).isEmpty || newName.count > 120)
                }
            }
            .padding(Spacing.xl)
            .frame(width: 380)
        }
    }

    private var header: some View {
        HStack(alignment: .top, spacing: Spacing.m) {
            Image(systemName: EntitySymbol.of(summary.entityType))
                .font(.system(size: 22, weight: .light))
                .foregroundStyle(Color.accentColor)
                .frame(width: 44, height: 44)
                .background(Color.accentColor.opacity(0.10), in: Circle())
            VStack(alignment: .leading, spacing: Spacing.xs) {
                Text(summary.name).font(.pageTitle).textSelection(.enabled)
                HStack(spacing: Spacing.s) {
                    StatusPill(text: KnowledgeVocabulary.label(summary.entityType).capitalized, tone: .accent)
                    Text("\(TypeOrigin.label(summary.typeOrigin)) · \(Count.label(summary.mentions, "mention")) in \(Count.label(summary.sources, "source"))")
                        .font(.meta).foregroundStyle(.secondary)
                    if summary.status != "active" { StatusPill(text: summary.status.capitalized, tone: .neutral) }
                }
                if detail.aliases.count > 1 {
                    Text("Also known as \(detail.aliases.filter { $0 != summary.name }.joined(separator: ", "))")
                        .font(.meta).foregroundStyle(.secondary)
                }
            }
            Spacer(minLength: Spacing.l)
            Button("Explore connections", action: exploreConnections)
            Button("Ask a Stronger Model…") { verification.requestForEntity(summary.entityId) }
                .help("Creates a verification request for review. Nothing is sent until you approve it.")
            Menu {
                Menu("Set Type") {
                    ForEach(KnowledgeVocabulary.entityTypes, id: \.self) { type in
                        Button(KnowledgeVocabulary.label(type).capitalized) { model.setType(type) }
                    }
                }
                Button("Rename…") {
                    newName = summary.name
                    renaming = true
                }
                Menu("Merge Into") {
                    ForEach(others) { other in
                        Button("\(other.name) (\(KnowledgeVocabulary.label(other.entityType)))") { model.merge(into: other) }
                    }
                }
                .disabled(others.isEmpty)
                Divider()
                if summary.status == "rejected" {
                    Button("Restore") { model.restore() }
                } else {
                    Button("Not a Real Entity", role: .destructive) { model.reject() }
                }
            } label: {
                Image(systemName: "ellipsis.circle")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .accessibilityLabel("Correct entity")
        }
    }

    private var mentions: some View {
        Panel(title: "Mentions", systemImage: "text.quote", caption: Count.label(detail.mentions.count, "mention")) {
            if detail.mentions.isEmpty {
                Text("No visible evidence. It may come from a revoked source, a newer revision, or a rejected mention.")
                    .font(.callout).foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: Spacing.m) {
                ForEach(Array(detail.mentions.prefix(showAllMentions ? detail.mentions.count : 3))) { mention in
                    HStack(alignment: .top, spacing: Spacing.s) {
                        RoundedRectangle(cornerRadius: 1).fill(Color.accentColor.opacity(0.5)).frame(width: 2)
                        VStack(alignment: .leading, spacing: Spacing.xxs) {
                            Text(mention.evidence).font(.callout).textSelection(.enabled)
                            Text([mention.sourceName, "as \(KnowledgeVocabulary.label(mention.mentionType))",
                                  MentionStatus.label(mention.status), mention.verdict.map { "verifier: \($0)" }]
                                .compactMap { $0 }.joined(separator: " · "))
                                .font(.caption2).foregroundStyle(.tertiary)
                        }
                    }
                }
                if detail.mentions.count > 3 {
                    Button(showAllMentions ? "Show fewer excerpts" : "Show all \(detail.mentions.count) excerpts") {
                        showAllMentions.toggle()
                    }
                }
                if detail.mentions.count == 200 {
                    Text("Showing the first 200 evidence mentions. This is not the complete source history.")
                        .font(.caption2).foregroundStyle(.tertiary)
                }
            }
        }
    }

    private var relations: some View {
        Panel(title: "Relationships", systemImage: "arrow.triangle.branch",
              caption: "\(relationGroups.count) assertions · \(detail.relations.count) observations") {
            Menu {
                Picker("Relation", selection: $predicate) {
                    ForEach(KnowledgeVocabulary.predicates, id: \.self) { Text(KnowledgeVocabulary.label($0)).tag($0) }
                }
                Divider()
                ForEach(others) { other in
                    Button("\(summary.name) \(KnowledgeVocabulary.label(predicate)) \(other.name)") { model.relate(predicate, to: other) }
                }
            } label: {
                Label("Add", systemImage: "plus")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .disabled(others.isEmpty || summary.status != "active")
        } content: {
            VStack(alignment: .leading, spacing: Spacing.s) {
                ForEach(relationGroups) { group in
                    VStack(alignment: .leading, spacing: Spacing.s) {
                        HStack(spacing: Spacing.xs) {
                            Text(group.outgoing ? summary.name : group.otherName).font(.rowTitle)
                            Text(KnowledgeVocabulary.label(group.predicate)).foregroundStyle(.secondary)
                            Text(group.outgoing ? group.otherName : summary.name).font(.rowTitle)
                        }
                        if group.observations.count == 1, let observation = group.observations.first {
                            relationObservation(observation)
                        } else {
                            DisclosureGroup("\(group.observations.count) separate observations") {
                                VStack(alignment: .leading, spacing: Spacing.s) {
                                    ForEach(group.observations) { observation in
                                        relationObservation(observation)
                                    }
                                }
                                .padding(.top, Spacing.xs)
                            }
                            .font(.caption)
                        }
                    }
                    .padding(.vertical, Spacing.xs)
                }
                if detail.relations.count == 200 {
                    Text("Showing the first 200 relation observations. Additional evidence may exist.")
                        .font(.caption2).foregroundStyle(.tertiary)
                }
            }
        }
    }

    private func relationObservation(_ relation: EntityRelation) -> some View {
        VStack(alignment: .leading, spacing: Spacing.xxs) {
            Text(relation.origin == "owner" ? "Owner assertion · no source citation" : "Source observation")
                .font(.caption2).foregroundStyle(.tertiary)
            if let evidence = relation.evidence {
                Text(evidence).font(.caption).foregroundStyle(.secondary)
                    .textSelection(.enabled).lineLimit(4)
            }
        }
    }

    private func rename() {
        guard !newName.trimmingCharacters(in: .whitespaces).isEmpty, newName.count <= 120 else { return }
        model.rename(to: newName)
        renaming = false
    }
}

/// How a mention came to count: verified by intake, confirmed or renamed by the owner, or still a guess.
enum MentionStatus {
    static func label(_ status: String) -> String {
        switch status {
        case "verified": "verified by intake"
        case "documented": "read directly from source"
        case "accepted": "confirmed by you"
        case "corrected": "renamed by you"
        case "pending": "unconfirmed guess"
        default: status
        }
    }
}
