import AppKit
import SwiftUI

struct SourceDetailView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @State private var sourceToRevoke: LibraryItem?
    @State private var confirmRevoke = false

    var body: some View {
        ScrollView {
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
                        Button(model.exportingOriginal ? "Exporting…" : "Export original…") {
                            chooseExportDestination(for: item)
                        }
                        .disabled(model.exportingOriginal || item.sourceStatus != "active")
                        Button("Verify original") { model.verify() }
                        Button("Retry extraction") { model.retry() }
                        Button("Revoke…", role: .destructive) {
                            sourceToRevoke = item
                            confirmRevoke = true
                        }
                        .disabled(item.sourceStatus != "active")
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
                    if !model.sourceMoves.isEmpty {
                        DisclosureGroup("Observed file moves (\(model.sourceMoves.count))") {
                            ForEach(model.sourceMoves) { move in
                                VStack(alignment: .leading, spacing: 3) {
                                    Text(move.previousPath).font(.caption).textSelection(.enabled)
                                    Text("Moved to: \(move.currentPath)")
                                        .font(.caption).textSelection(.enabled)
                                    Text("Digest \(move.sha256) · \(move.recordedUtc)")
                                        .font(.caption).foregroundStyle(.secondary)
                                        .textSelection(.enabled)
                                }
                            }
                        }
                    }
                    if item.isVideo {
                        Divider()
                        Text("Media original").font(.headline)
                        Button(model.inspectingMedia ? "Inspecting media…" :
                               "Inspect verified media header") {
                            model.inspectSelectedMedia()
                        }
                        .disabled(item.sourceStatus != "active" || model.inspectingMedia)
                        if let inspected = model.mediaInspection,
                           inspected.sourceId == item.sourceId,
                           inspected.revision == item.revision,
                           inspected.sourceSha256 == item.sha256 {
                            Text("Signature: \(inspected.outcome) · \(inspected.containerFamily ?? "unrecognized")")
                                .font(.caption).textSelection(.enabled)
                            if let duration = inspected.durationMilliseconds {
                                Text("Container timeline: \(Double(duration) / 1000, specifier: "%.3f") seconds")
                                    .font(.caption).textSelection(.enabled)
                            } else {
                                Text("Container duration unavailable from the bounded header.")
                                    .font(.caption).foregroundStyle(.secondary)
                            }
                            Text("Source \(inspected.itemId) · \(inspected.originalBytes) verified bytes · \(inspected.producer)")
                                .font(.caption).textSelection(.enabled)
                        }
                        Text("Header recognition is not a decoder, transcript verification or permission to play.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                    ProposalReviewView()
                    Text("Extracted text preview").font(.headline)
                    Text(model.libraryPreview)
                        .font(.system(.body, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                } else {
                    Text("Select a captured source to inspect it.")
                        .foregroundStyle(.secondary)
                }
            }.frame(maxWidth: .infinity, alignment: .leading)
        }
        .confirmationDialog("Revoke this source from active retrieval?",
                            isPresented: $confirmRevoke,
                            presenting: sourceToRevoke) { item in
            Button("Revoke source", role: .destructive) { model.revoke(item) }
        } message: { item in
            Text("Remove \(item.title) from search and derived text. Its stored original is retained.")
        }
    }

    private func chooseExportDestination(for item: LibraryItem) {
        let picker = NSSavePanel()
        picker.nameFieldStringValue = item.title
        picker.canCreateDirectories = true
        picker.prompt = "Export original"
        picker.message = "Praxis verifies the preserved original before saving. Existing files will not be overwritten. Files saved to a synced folder may be uploaded by that service."
        guard picker.runModal() == .OK, let destination = picker.url else { return }
        model.exportOriginal(item, destination: destination)
    }
}
