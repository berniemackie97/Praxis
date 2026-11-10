import SwiftUI

/// Entities across all captured sources, with type, evidence counts and conflicts.
struct KnowledgeView: View {
    @ObservedObject var model: KnowledgeWorkbenchModel
    let verification: VerificationWorkbenchModel

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    TextField("Search names", text: $model.search)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit { model.refresh() }
                    Button("Refresh") { model.refresh() }
                }
                Picker("Type", selection: Binding(
                    get: { model.typeFilter },
                    set: { model.setTypeFilter($0) }
                )) {
                    Text("All types").tag("")
                    ForEach(KnowledgeVocabulary.entityTypes, id: \.self) { type in
                        Text(KnowledgeVocabulary.label(type)).tag(type)
                    }
                }
                if model.entities.isEmpty {
                    Text("No entities yet. They appear once the local model has enriched captured text.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                List(model.entities) { entity in
                    Button { model.select(entity) } label: {
                        EntityRow(entity: entity)
                    }
                    .buttonStyle(.plain)
                    .listRowBackground(model.selected?.summary.entityId == entity.entityId
                        ? Color.accentColor.opacity(0.15) : Color.clear)
                }
                FeedbackLine(error: model.error, notice: model.notice)
            }
            .frame(width: 320)
            Divider()
            if let detail = model.selected {
                EntityDetailView(detail: detail, model: model, verification: verification)
            } else {
                Text("Select an entity to see where it is mentioned, how it relates to others, and to correct it.")
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }
        }
        .onAppear { model.refresh() }
    }
}

private struct EntityRow: View {
    let entity: EntitySummary

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 6) {
                Text(entity.name).fontWeight(.medium)
                if entity.typeConflict {
                    Text("conflicting types").font(.caption2).foregroundColor(.orange)
                }
            }
            Text("\(KnowledgeVocabulary.label(entity.entityType)) · \(TypeOrigin.label(entity.typeOrigin)) · \(Count.label(entity.mentions, "mention")) · \(Count.label(entity.sources, "source"))")
                .font(.caption).foregroundStyle(.secondary).lineLimit(1)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .contentShape(Rectangle())
    }
}

enum Count {
    static func label(_ count: Int, _ noun: String) -> String {
        "\(count) \(noun)\(count == 1 ? "" : "s")"
    }
}

enum TypeOrigin {
    static func label(_ origin: String) -> String {
        switch origin {
        case "owner": "set by you"
        case "verified": "verified"
        default: "local model"
        }
    }
}
