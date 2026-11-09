import SwiftUI

struct LibraryView: View {
    @EnvironmentObject private var model: WorkbenchModel
    let openQuickNote: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            VStack {
                HStack {
                    Text("Captured sources").font(.headline)
                    Spacer()
                    Button("New note", action: openQuickNote)
                    Button("Refresh") { model.refresh() }
                }
                Picker("Collection", selection: Binding(
                    get: { model.selectedCollectionId },
                    set: { model.selectCollectionFilter($0) }
                )) {
                    Text("All sources").tag("")
                    ForEach(model.collections) { collection in
                        Text(collection.name).tag(collection.collectionId)
                    }
                }
                if model.items.isEmpty {
                    Text("No captured sources in this view yet.")
                        .font(.caption).foregroundStyle(.secondary)
                }
                List(model.items) { item in
                    Button { model.select(item) } label: {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(item.title).fontWeight(.medium)
                            Text("Revision \(item.revision) · \(item.processingState ?? "pending") · \(item.extractionOutcome ?? "not extracted")")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .listRowBackground(model.selected?.id == item.id ? Color.accentColor.opacity(0.15) : Color.clear)
                }
                HStack {
                    Button("Previous") {
                        model.page -= 1
                        model.refresh()
                    }.disabled(model.page == 0)
                    Spacer()
                    Text("Page \(model.page + 1)").font(.caption)
                    Spacer()
                    Button("Next") {
                        model.page += 1
                        model.refresh()
                    }.disabled(model.items.count < 100)
                }
            }.frame(width: 310)
            Divider()
            SourceDetailView()
        }
    }
}
