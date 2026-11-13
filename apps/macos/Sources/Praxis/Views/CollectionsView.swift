import SwiftUI

struct CollectionsView: View {
    @EnvironmentObject private var model: WorkbenchModel
    let showInLibrary: (String) -> Void
    @State private var editing: CollectionEdit?
    @State private var archiving: SourceCollection?

    var body: some View {
        Group {
            if model.collections.isEmpty {
                EmptyState(systemImage: "folder.badge.plus", title: "No collections yet",
                           message: "Collections are your own groupings. Importing or updating files never changes originals or sorts them for you.",
                           actionTitle: "New Collection") { editing = CollectionEdit(collection: nil) }
            } else {
                List {
                    ForEach(model.collections) { collection in
                        ItemRow(systemImage: "folder", title: collection.name,
                                subtitle: Count.label(collection.activeSourceCount, "source"), tint: .accentColor) {
                            Button("Show in Library") { showInLibrary(collection.collectionId) }
                                .controlSize(.small)
                        }
                        .contextMenu {
                            Button("Show in Library") { showInLibrary(collection.collectionId) }
                            Button("Rename…") { editing = CollectionEdit(collection: collection) }
                            Divider()
                            Button("Archive…", role: .destructive) { archiving = collection }
                        }
                        .onTapGesture(count: 2) { showInLibrary(collection.collectionId) }
                    }
                }
                .listStyle(.inset(alternatesRowBackgrounds: false))
            }
        }
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button { editing = CollectionEdit(collection: nil) } label: { Label("New Collection", systemImage: "folder.badge.plus") }
            }
        }
        .sheet(item: $editing) { edit in
            CollectionNameSheet(edit: edit) { name in
                if let collection = edit.collection {
                    model.renameCollection(collection.collectionId, name: name)
                } else {
                    model.createCollection(name)
                }
                editing = nil
            } cancel: { editing = nil }
        }
        .confirmationDialog("Archive “\(archiving?.name ?? "")”?", isPresented: Binding(
            get: { archiving != nil }, set: { if !$0 { archiving = nil } })) {
            Button("Archive Collection", role: .destructive) {
                if let archiving { model.archiveCollection(archiving.collectionId) }
                archiving = nil
            }
        } message: {
            Text("This hides the collection. Its sources and original files remain preserved.")
        }
    }
}

private struct CollectionEdit: Identifiable {
    let collection: SourceCollection?
    var id: String { collection?.collectionId ?? "new" }
}

private struct CollectionNameSheet: View {
    let edit: CollectionEdit
    let save: (String) -> Void
    let cancel: () -> Void
    @State private var name = ""

    private var trimmed: String { name.trimmingCharacters(in: .whitespacesAndNewlines) }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.l) {
            Text(edit.collection == nil ? "New collection" : "Rename collection").font(.headline)
            TextField("Name", text: $name).textFieldStyle(.roundedBorder).onSubmit { if !trimmed.isEmpty { save(trimmed) } }
            HStack {
                Spacer()
                Button("Cancel", role: .cancel, action: cancel).keyboardShortcut(.cancelAction)
                Button(edit.collection == nil ? "Create" : "Rename") { save(trimmed) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(trimmed.isEmpty)
            }
        }
        .padding(Spacing.xl)
        .frame(width: 380)
        .onAppear { name = edit.collection?.name ?? "" }
    }
}
