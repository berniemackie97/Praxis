import AppKit
import SwiftUI

/// Catalog location, intake controls and the health of each watched folder.
struct ConfigurationView: View {
    @EnvironmentObject private var model: WorkbenchModel
    let dataRoot: String
    @Binding var rootApprovals: String

    var body: some View {
        Form {
            Section {
                LabeledContent("Location") {
                    HStack {
                        Text(dataRoot).lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                        Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: dataRoot)]) }
                            .controlSize(.small)
                    }
                }
            } header: {
                Text("Private catalog")
            }

            Section {
                LabeledContent("Status") {
                    StatusPill(text: IntakeHealth.label(model), tone: IntakeHealth.tone(model))
                }
                if let status = model.intakeStatus {
                    LabeledContent("Files", value: "\(status.observedFiles.formatted()) observed · \(status.importedSinceStart.formatted()) captured this session")
                }
                if let backlog = model.intakeBacklog {
                    LabeledContent("Processing", value: "\(backlog.queued) queued · \(backlog.running) running · \(backlog.failed) failed"
                        + ((backlog.enrichmentPending ?? 0) > 0 ? " · local enrichment \(backlog.enrichmentPending!) left" : ""))
                }
                HStack {
                    Button(model.intakeStatus?.paused == true ? "Resume Intake" : "Pause Intake") {
                        model.pauseIntake(model.intakeStatus?.paused != true)
                    }
                    Button("Retry Scans") { model.retryIntakeScan() }
                        .disabled(model.intakeStatus?.paused == true)
                }
            } header: {
                Text("Intake")
            }

            Section {
                ForEach(model.intakeRoots) { root in
                    VStack(alignment: .leading, spacing: Spacing.xs) {
                        HStack {
                            Label(root.path, systemImage: "folder").lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                            Spacer()
                            StatusPill(text: root.status.state.replacingOccurrences(of: "_", with: " ").capitalized,
                                       tone: root.status.state == "watching" ? .positive : root.status.problem != nil || root.status.state == "reapproval_required" ? .warning : .neutral)
                        }
                        Text("\(root.status.observedFiles.formatted()) observed · \(root.status.importedSinceStart.formatted()) captured" +
                             (root.status.lastSuccessfulScanUtc.map { " · last scan \(DateText.relative($0))" } ?? ""))
                            .font(.meta).foregroundStyle(.secondary)
                        if let problem = root.status.problem {
                            Text(problem).font(.meta).foregroundStyle(.orange).textSelection(.enabled)
                        }
                        if root.status.state == "reapproval_required" {
                            Button("Review and Reapprove Folder…") { approve(root.path) }.controlSize(.small)
                        }
                        if root.status.errorCount > 0 {
                            DisclosureGroup("\(Count.label(root.status.errorCount, "file error"))") {
                                ForEach(root.status.fileErrors) { error in
                                    Text("\(error.sourcePath): \(error.message)").font(.caption).textSelection(.enabled)
                                }
                            }
                            .font(.meta)
                        }
                    }
                    .padding(.vertical, Spacing.xxs)
                }
                Button("Stop Praxis to Edit Folders…") { model.stop() }
            } header: {
                Text("Watched folders")
            } footer: {
                Text("Captured originals stay preserved when a folder disappears. A reconnected or replaced folder isn't scanned until its identity is confirmed.")
                    .font(.meta).foregroundStyle(.secondary)
            }

            if !model.intakeIncidents.isEmpty {
                Section {
                    ForEach(model.intakeIncidents.prefix(8)) { incident in
                        VStack(alignment: .leading, spacing: Spacing.xxs) {
                            Text("\(incident.state.replacingOccurrences(of: "_", with: " ")) · \(incident.rootPath)")
                                .font(.callout).lineLimit(1).truncationMode(.middle).textSelection(.enabled)
                            Text("Started \(DateText.short(incident.startedUtc)) · \(incident.resolvedUtc == nil ? "awaiting recovery" : "resolved")")
                                .font(.meta).foregroundStyle(.secondary)
                        }
                    }
                } header: {
                    Text("Recent incidents")
                }
            }
        }
        .formStyle(.grouped)
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
