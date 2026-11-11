import AppKit
import SwiftUI

struct SetupView: View {
    @EnvironmentObject private var model: WorkbenchModel
    @Binding var dataRoot: String
    @Binding var inbox: String
    @Binding var additionalInboxes: String
    @Binding var rootApprovals: String
    @Binding var configured: Bool
    @Binding var dotnetExecutable: String

    private var extraFolders: [String] {
        (try? WatchedFolderSettings.folders(additionalInboxes)) ?? []
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Set up your private Second Brain").font(.title2)
            Text("Choose a parent for a private PraxisData folder and separate existing watched folders. Use folders outside cloud-sync services if you do not want OS-level synchronization.")
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
                Button("Choose primary Inbox…") {
                    if let selected = chooseFolder() {
                        let canonical = CanonicalPath.of(selected.path)
                        inbox = canonical.path
                        rememberApproval(canonical)
                        configured = false
                    }
                }
                Text(inbox.isEmpty ? "Not selected" : inbox)
                    .textSelection(.enabled).lineLimit(2)
            }
            Text("The primary Inbox is also used for Quick note capture. Other approved folders are watched without copying or moving their contents.")
                .font(.caption).foregroundStyle(.secondary)
            ForEach(extraFolders, id: \.self) { path in
                HStack {
                    Text(path).textSelection(.enabled).lineLimit(1)
                    Spacer()
                    Button("Remove") {
                        additionalInboxes = WatchedFolderSettings.encode(extraFolders.filter { $0 != path })
                        configured = false
                    }
                }
            }
            Button("Add watched folder…") {
                if let selected = chooseFolder() {
                    let canonical = CanonicalPath.of(selected.path)
                    var folders = extraFolders
                    folders.append(canonical.path)
                    additionalInboxes = WatchedFolderSettings.encode(folders)
                    rememberApproval(canonical)
                    configured = false
                }
            }
            .disabled(extraFolders.count >= 3)
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
                model.start(dataRoot: dataRoot, inbox: inbox,
                            additionalInboxes: additionalInboxes, rootApprovals: rootApprovals,
                            initialize: !configured, dotnetExecutable: dotnetExecutable)
                if model.running { configured = true }
            }
            .buttonStyle(.borderedProminent)
            .disabled(dataRoot.isEmpty || inbox.isEmpty)
            Spacer()
        }.frame(maxWidth: .infinity, alignment: .leading)
    }

    private func rememberApproval(_ folder: URL) {
        guard let signature = WatchedFolderSettings.identity(folder) else { return }
        var approved = (try? WatchedFolderSettings.approvals(rootApprovals)) ?? [:]
        approved[folder.path] = signature
        rootApprovals = WatchedFolderSettings.encode(approved)
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
