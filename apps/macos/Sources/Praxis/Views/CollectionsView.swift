import SwiftUI

struct CollectionsView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @State private var confirmArchive = false
    @State private var newCollectionName = ""
    @State private var editingCollectionId = ""
    @State private var renameCollectionName = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Your collections").font(.title2.weight(.semibold))
            Text("Collections are your decisions. Importing or updating files never changes the original bytes or silently categorizes them.")
                .foregroundStyle(.secondary)
            HStack {
                TextField("New collection name", text: $newCollectionName)
                    .textFieldStyle(.roundedBorder)
                Button("Create") {
                    model.createCollection(newCollectionName)
                }
                .disabled(newCollectionName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            List(model.collections) { collection in
                HStack {
                    Text(collection.name)
                    Spacer()
                    Text("\(collection.activeSourceCount) active sources")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            Picker("Edit collection", selection: Binding(
                get: { editingCollectionId },
                set: { selectCollection($0) }
            )) {
                Text("Choose…").tag("")
                ForEach(model.collections) { collection in
                    Text(collection.name).tag(collection.collectionId)
                }
            }
            HStack {
                TextField("Collection name", text: $renameCollectionName)
                    .textFieldStyle(.roundedBorder)
                    .disabled(editingCollectionId.isEmpty)
                Button("Rename") {
                    model.renameCollection(editingCollectionId, name: renameCollectionName)
                }
                .disabled(editingCollectionId.isEmpty ||
                    renameCollectionName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                Button("Archive…", role: .destructive) { confirmArchive = true }
                    .disabled(editingCollectionId.isEmpty)
            }
            Spacer()
        }
        .confirmationDialog("Archive this collection?", isPresented: $confirmArchive) {
            Button("Archive collection", role: .destructive) {
                model.archiveCollection(editingCollectionId)
                selectCollection("")
            }
        } message: {
            Text("This hides the collection. Its sources and original files remain preserved.")
        }
    }

    private func selectCollection(_ id: String) {
        editingCollectionId = id
        renameCollectionName = model.collections.first { $0.collectionId == id }?.name ?? ""
    }
}
