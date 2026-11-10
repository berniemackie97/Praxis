import SwiftUI

/// One entity: aliases, evidence, relations, co-mentions and the owner's corrections.
struct EntityDetailView: View {
    let detail: EntityDetail
    @ObservedObject var model: KnowledgeWorkbenchModel
    let verification: VerificationWorkbenchModel
    @State private var newName = ""
    @State private var predicate = KnowledgeVocabulary.predicates[0]

    private var summary: EntitySummary { detail.summary }
    private var others: [EntitySummary] { model.entities.filter { $0.entityId != summary.entityId } }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                header
                corrections
                section("Mentions (\(detail.mentions.count))") {
                    if detail.mentions.isEmpty {
                        Text("No visible evidence. It may come from a revoked source, a newer revision, or a rejected mention.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    ForEach(detail.mentions) { mention in
                        VStack(alignment: .leading, spacing: 2) {
                            Text("“\(mention.evidence)”").textSelection(.enabled)
                            Text([mention.sourceName, "as \(KnowledgeVocabulary.label(mention.mentionType))",
                                  mention.status, mention.verdict.map { "verifier: \($0)" }]
                                .compactMap { $0 }.joined(separator: " · "))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
                section("Relations (\(detail.relations.count))") {
                    ForEach(detail.relations) { relation in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(relation.outgoing
                                ? "\(summary.name) \(KnowledgeVocabulary.label(relation.predicate)) \(relation.otherName)"
                                : "\(relation.otherName) \(KnowledgeVocabulary.label(relation.predicate)) \(summary.name)")
                            Text(relation.origin == "owner" ? "added by you" : "“\(relation.evidence ?? "")”")
                                .font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                        }
                    }
                    HStack {
                        Picker("", selection: $predicate) {
                            ForEach(KnowledgeVocabulary.predicates, id: \.self) { value in
                                Text(KnowledgeVocabulary.label(value)).tag(value)
                            }
                        }
                        .labelsHidden()
                        .frame(width: 150)
                        Menu("Add relation to…") {
                            ForEach(others) { other in
                                Button(other.name) { model.relate(predicate, to: other) }
                            }
                        }
                        .disabled(others.isEmpty || summary.status != "active")
                    }
                }
                if !detail.related.isEmpty {
                    section("Mentioned together with") {
                        Text(detail.related.map { "\($0.name) (\($0.sharedSegments))" }.joined(separator: ", "))
                            .font(.caption)
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(summary.name).font(.title2.weight(.semibold)).textSelection(.enabled)
            Text("\(KnowledgeVocabulary.label(summary.entityType)) · \(TypeOrigin.label(summary.typeOrigin)) · \(summary.status)")
                .foregroundStyle(.secondary)
            if summary.typeConflict {
                Text("The local model typed this inconsistently. Set the type yourself, or ask a stronger model to settle it.")
                    .font(.caption).foregroundColor(.orange)
            }
            if detail.aliases.count > 1 {
                Text("Also known as: \(detail.aliases.filter { $0 != summary.name }.joined(separator: ", "))")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private var corrections: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Menu("Set type") {
                    ForEach(KnowledgeVocabulary.entityTypes, id: \.self) { type in
                        Button(KnowledgeVocabulary.label(type)) { model.setType(type) }
                    }
                }
                Menu("Merge into…") {
                    ForEach(others) { other in
                        Button("\(other.name) (\(KnowledgeVocabulary.label(other.entityType)))") { model.merge(into: other) }
                    }
                }
                .disabled(others.isEmpty)
                if summary.status == "rejected" {
                    Button("Restore") { model.restore() }
                } else {
                    Button("Not a real entity") { model.reject() }
                }
                Button("Ask a stronger model…") { verification.requestForEntity(summary.entityId) }
                    .help("Creates a verification request for review. Nothing is sent until you approve it.")
            }
            HStack {
                TextField("Rename", text: $newName).textFieldStyle(.roundedBorder).frame(maxWidth: 260)
                Button("Rename") {
                    model.rename(to: newName)
                    newName = ""
                }
                .disabled(newName.trimmingCharacters(in: .whitespaces).isEmpty || newName.count > 120)
            }
        }
    }

    private func section<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.headline)
            content()
        }
    }
}
