import AppKit
import Combine
import SwiftUI

struct PraxisWindow: View {
    @EnvironmentObject private var model: Workbench
    @AppStorage("dataRoot") private var dataRoot = ""
    @AppStorage("inbox") private var inbox = ""
    @AppStorage("configured") private var configured = false
    @AppStorage("dotnetExecutable") private var dotnetExecutable = ""
    @State private var section = 0
    @State private var confirmRevoke = false
    @State private var confirmArchive = false
    @State private var newCollectionName = ""
    @State private var editingCollectionId = ""
    @State private var renameCollectionName = ""
    private let refreshTimer = Timer.publish(every: 5, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                VStack(alignment: .leading) {
                    Text("Praxis").font(.largeTitle.weight(.semibold))
                    Text("Local knowledge workbench").foregroundStyle(.secondary)
                }
                Spacer()
                Text(model.running ? "Intake active" : "Not running")
                    .foregroundColor(model.running ? Color.green : Color.secondary)
            }
            if model.running {
                Picker("Workspace", selection: $section) {
                    Text("Inbox & Library").tag(0)
                    Text("Quick note").tag(1)
                    Text("Search").tag(2)
                    Text("Collections").tag(3)
                    Text("Configuration").tag(4)
                }.pickerStyle(.segmented)
                if section == 0 { library }
                if section == 1 { quickNote }
                if section == 2 { search }
                if section == 3 { collectionManagement }
                if section == 4 { configuration }
            } else {
                setup
            }
            Divider()
            Text(model.message).font(.footnote).foregroundStyle(.secondary)
                .lineLimit(3).textSelection(.enabled)
        }
        .padding(20)
        .onAppear {
            if configured && !dataRoot.isEmpty && !inbox.isEmpty {
                model.start(dataRoot: dataRoot, inbox: inbox, initialize: false,
                            dotnetExecutable: dotnetExecutable)
            }
        }
        .onDisappear { model.stop() }
        .onReceive(refreshTimer) { _ in
            if model.running { model.refresh() }
        }
        .confirmationDialog("Revoke this source from active retrieval?",
                            isPresented: $confirmRevoke) {
            Button("Revoke source", role: .destructive) { model.revoke() }
        } message: {
            Text("Search and derived text will no longer expose this source. Its stored original bytes are retained.")
        }
        .confirmationDialog("Archive this collection?", isPresented: $confirmArchive) {
            Button("Archive collection", role: .destructive) {
                model.archiveCollection(editingCollectionId)
                editingCollectionId = ""
                renameCollectionName = ""
            }
        } message: {
            Text("This hides the collection. Its sources and original files remain preserved.")
        }
    }

    private var setup: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Set up your private Second Brain").font(.title2)
            Text("Choose a parent for a private PraxisData folder and a separate existing Inbox. Use folders outside cloud-sync services if you do not want OS-level synchronization.")
                .foregroundStyle(.secondary)
            HStack {
                Button("Choose catalog location…") {
                    if let selected = chooseFolder() {
                        dataRoot = selected.appendingPathComponent("PraxisData").path
                        configured = false
                    }
                }
                Text(dataRoot.isEmpty ? "Not selected" : dataRoot)
                    .textSelection(.enabled).lineLimit(2)
            }
            HStack {
                Button("Choose watched Inbox…") {
                    if let selected = chooseFolder() {
                        inbox = selected.path
                        configured = false
                    }
                }
                Text(inbox.isEmpty ? "Not selected" : inbox)
                    .textSelection(.enabled).lineLimit(2)
            }
            HStack {
                Button("Locate .NET executable (optional)…") {
                    let picker = NSOpenPanel()
                    picker.canChooseFiles = true
                    picker.canChooseDirectories = false
                    picker.allowsMultipleSelection = false
                    if picker.runModal() == .OK, let selected = picker.url {
                        dotnetExecutable = selected.path
                    }
                }
                Text(dotnetExecutable.isEmpty ? "Using standard .NET locations" : dotnetExecutable)
                    .font(.caption).textSelection(.enabled).lineLimit(2)
            }
            Button("Start Praxis") {
                model.start(dataRoot: dataRoot, inbox: inbox, initialize: !configured,
                            dotnetExecutable: dotnetExecutable)
                if model.running { configured = true }
            }
            .buttonStyle(.borderedProminent)
            .disabled(dataRoot.isEmpty || inbox.isEmpty)
            Spacer()
        }.frame(maxWidth: .infinity, alignment: .leading)
    }

    private var library: some View {
        HStack(alignment: .top, spacing: 14) {
            VStack {
                HStack {
                    Text("Captured sources").font(.headline)
                    Spacer()
                    Button("New note") { section = 1 }
                    Button("Refresh") { model.refresh() }
                }
                Picker("Collection", selection: $model.selectedCollectionId) {
                    Text("All sources").tag("")
                    ForEach(model.collections) { collection in
                        Text(collection.name).tag(collection.collectionId)
                    }
                }
                .onChange(of: model.selectedCollectionId) { _ in
                    model.page = 0
                    model.refresh()
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
                    }.buttonStyle(.plain)
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
            VStack(alignment: .leading, spacing: 10) {
                if let item = model.selected {
                    Text(item.title).font(.title3.weight(.semibold))
                    Text(item.sourceLocator).font(.caption).textSelection(.enabled)
                    Text("Source \(item.sourceId) · Revision \(item.revision)")
                        .font(.caption).textSelection(.enabled)
                    Text("Original \(item.originalBytes) bytes · SHA-256 \(item.sha256)")
                        .font(.caption).textSelection(.enabled)
                    Text("State: \(item.sourceStatus) / \(item.processingState ?? "pending") / \(item.extractionOutcome ?? "unknown")")
                        .font(.caption)
                    HStack {
                        Button("Verify original") { model.verify() }
                        Button("Retry extraction") { model.retry() }
                        Button("Revoke…", role: .destructive) { confirmRevoke = true }
                    }
                    Text("Source collections").font(.headline)
                    Text(model.collections
                        .filter { model.assignedCollectionIds.contains($0.collectionId) }
                        .map(\.name).joined(separator: ", "))
                        .font(.caption).foregroundStyle(.secondary)
                    HStack {
                        Picker("Collection", selection: $model.selectedMembershipId) {
                            Text("Choose…").tag("")
                            ForEach(model.collections) { collection in
                                Text(collection.name).tag(collection.collectionId)
                            }
                        }
                        Button("Add") { model.addMembership() }
                            .disabled(model.selectedMembershipId.isEmpty ||
                                      item.sourceStatus != "active")
                        Button("Remove") { model.removeMembership() }
                            .disabled(!model.assignedCollectionIds
                                .contains(model.selectedMembershipId))
                    }
                    Text("Extracted text preview").font(.headline)
                    ScrollView {
                        Text(model.preview)
                            .font(.system(.body, design: .monospaced))
                            .textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                } else {
                    Text("Select a captured source to inspect it.")
                        .foregroundStyle(.secondary)
                }
                Spacer(minLength: 0)
            }.frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var quickNote: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Capture a thought").font(.title2.weight(.semibold))
            Text("Type or paste Markdown, a quotation, or a URL. Nothing reads your clipboard automatically, fetches websites, or sends content to a model.")
                .foregroundStyle(.secondary)
            TextField("Title (optional)", text: $model.noteTitle)
                .textFieldStyle(.roundedBorder)
                .disabled(model.savingNote)
            TextEditor(text: $model.noteText)
                .font(.system(.body, design: .monospaced))
                .disabled(model.savingNote)
                .border(Color.secondary.opacity(0.3))
                .accessibilityLabel("Note content")
            Text("Saving creates a private Markdown original in your approved Inbox. The optional title becomes its heading. Use Command-Return to save.")
                .font(.footnote).foregroundStyle(.secondary)
            HStack {
                Button(model.savingNote ? "Saving…" : "Save note") {
                    model.saveNote()
                }
                .buttonStyle(.borderedProminent)
                .keyboardShortcut(.return, modifiers: [.command])
                .disabled(model.savingNote ||
                    model.noteText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                    model.noteText.count > 8_000 ||
                    model.noteTitle.count > 80)
                Button("Discard draft", role: .destructive) { model.discardNote() }
                    .disabled(model.savingNote ||
                        (model.noteText.isEmpty && model.noteTitle.isEmpty))
                Spacer()
                Text("\(model.noteText.count) / 8,000 characters")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private var search: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                TextField("Search extracted text", text: $model.query)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { model.search() }
                Button("Search") { model.search() }
            }
            HStack(alignment: .top) {
                List(model.hits) { hit in
                    Button { model.previewHit(hit) } label: {
                        VStack(alignment: .leading, spacing: 6) {
                            Text(hit.title).fontWeight(.medium)
                            Text(hit.sourceLocator).font(.caption).foregroundStyle(.secondary)
                            Text(hit.snippet).font(.callout).lineLimit(3)
                        }
                    }.buttonStyle(.plain)
                }.frame(minWidth: 350)
                Divider()
                ScrollView {
                    Text(model.preview).font(.system(.body, design: .monospaced))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                }
            }
        }
    }

    private var collectionManagement: some View {
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
            Picker("Edit collection", selection: $editingCollectionId) {
                Text("Choose…").tag("")
                ForEach(model.collections) { collection in
                    Text(collection.name).tag(collection.collectionId)
                }
            }
            .onChange(of: editingCollectionId) { id in
                renameCollectionName = model.collections.first(where: {
                    $0.collectionId == id
                })?.name ?? ""
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
    }

    private var configuration: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Private catalog").font(.headline)
            Text(dataRoot).textSelection(.enabled)
            Text("Approved watched folder").font(.headline)
            Text(inbox).textSelection(.enabled)
            Text("No browser service, remote model, connector or agent credential is enabled.")
                .foregroundStyle(.secondary)
            Spacer()
        }.frame(maxWidth: .infinity, alignment: .leading)
    }

    private func chooseFolder() -> URL? {
        let picker = NSOpenPanel()
        picker.canChooseDirectories = true
        picker.canChooseFiles = false
        picker.allowsMultipleSelection = false
        picker.canCreateDirectories = true
        return picker.runModal() == .OK ? picker.url : nil
    }
}
