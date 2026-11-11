import AppKit
import SwiftUI

struct ConfigurationView: View {
    @EnvironmentObject private var model: WorkbenchModel
    let dataRoot: String
    @Binding var rootApprovals: String

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Private catalog").font(.headline)
            Text(dataRoot).textSelection(.enabled)
            Text("Approved watched folders").font(.headline)
            HStack {
                Button(model.intakeStatus?.paused == true ? "Resume intake" : "Pause intake") {
                    model.pauseIntake(model.intakeStatus?.paused != true)
                }
                Button("Retry scans") { model.retryIntakeScan() }
                    .disabled(model.intakeStatus?.paused == true)
                Button("Stop to edit watched folders") { model.stop() }
            }
            if let status = model.intakeStatus {
                Text("Overall: \(status.state) · \(status.observedFiles) observed · \(status.importedSinceStart) captured this session")
                    .textSelection(.enabled)
            }
            if let backlog = model.intakeBacklog {
                Text("Processing: \(backlog.queued) queued · \(backlog.running) running · \(backlog.failed) failed")
                    .textSelection(.enabled)
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    ForEach(model.intakeRoots) { root in
                        VStack(alignment: .leading, spacing: 4) {
                            Text(root.path).font(.headline).textSelection(.enabled)
                            Text("\(root.status.state) · \(root.status.observedFiles) observed · \(root.status.importedSinceStart) captured")
                                .textSelection(.enabled)
                            if let last = root.status.lastSuccessfulScanUtc {
                                Text("Last completed scan: \(last)")
                                    .font(.caption).foregroundStyle(.secondary)
                                    .textSelection(.enabled)
                            }
                            if let problem = root.status.problem {
                                Text(problem).foregroundColor(.orange).textSelection(.enabled)
                            }
                            if root.status.state == "reapproval_required" {
                                Button("Review and reapprove folder…") { approve(root.path) }
                            }
                            if root.status.errorCount > 0 {
                                Text("\(root.status.errorCount) file errors; showing up to eight")
                                    .font(.caption)
                                ForEach(root.status.fileErrors) { error in
                                    Text("\(error.sourcePath): \(error.message)")
                                        .font(.caption).textSelection(.enabled)
                                }
                            }
                        }
                        Divider()
                    }
                }.frame(maxWidth: .infinity, alignment: .leading)
            }
            Text("Previously captured originals remain preserved when a folder disappears. Reconnected or replaced folders are not scanned until their identity is confirmed. An unchanged folder can be reapproved automatically using its saved local filesystem identity. Editing the watched set requires stopping the worker.")
                .font(.caption).foregroundStyle(.secondary)
            Text("No browser service, remote model, connector or agent credential is enabled.")
                .foregroundStyle(.secondary)
            Spacer()
        }.frame(maxWidth: .infinity, alignment: .leading)
    }

    private func approve(_ path: String) {
        let picker = NSOpenPanel()
        picker.canChooseDirectories = true
        picker.canChooseFiles = false
        picker.allowsMultipleSelection = false
        guard picker.runModal() == .OK, let selected = picker.url else { return }
        let actual = CanonicalPath.of(selected.path)
        guard actual.path == path, let identity = WatchedFolderSettings.identity(actual) else {
            return
        }
        var approvals = (try? WatchedFolderSettings.approvals(rootApprovals)) ?? [:]
        approvals[path] = identity
        rootApprovals = WatchedFolderSettings.encode(approvals)
        model.approveIntakeRoot(path, identity: identity)
    }
}
