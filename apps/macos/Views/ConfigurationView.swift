import SwiftUI

struct ConfigurationView: View {
    @EnvironmentObject private var model: WorkbenchModel
    let dataRoot: String
    let inbox: String

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Private catalog").font(.headline)
            Text(dataRoot).textSelection(.enabled)
            Text("Approved watched folder").font(.headline)
            Text(inbox).textSelection(.enabled)
            Text("Intake health").font(.headline)
            HStack {
                Button(model.intakeStatus?.paused == true ? "Resume intake" : "Pause intake") {
                    model.pauseIntake(model.intakeStatus?.paused != true)
                }
                Button("Retry scan") { model.retryIntakeScan() }
                    .disabled(model.intakeStatus?.paused == true)
            }
            if let status = model.intakeStatus {
                Text("Status: \(status.state) · \(status.observedFiles) observed · \(status.importedSinceStart) captured this session")
                    .textSelection(.enabled)
                if let last = status.lastSuccessfulScanUtc {
                    Text("Last completed scan: \(last)")
                        .font(.caption).foregroundStyle(.secondary)
                        .textSelection(.enabled)
                }
                if let problem = status.problem {
                    Text(problem).foregroundColor(.orange).textSelection(.enabled)
                }
                if status.errorCount > 0 {
                    Text("\(status.errorCount) file error(s); showing up to eight")
                        .font(.headline)
                    ScrollView {
                        VStack(alignment: .leading, spacing: 8) {
                            ForEach(status.fileErrors) { error in
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(error.sourcePath).font(.caption)
                                        .textSelection(.enabled)
                                    Text(error.message).font(.caption)
                                        .foregroundStyle(.secondary).textSelection(.enabled)
                                }
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .frame(maxHeight: 160)
                }
            }
            Text("Reconnect an unavailable Inbox before retrying. Pause prevents new captures but does not cancel an import already in progress; previously queued text processing can finish.")
                .font(.caption).foregroundStyle(.secondary)
            Text("No browser service, remote model, connector or agent credential is enabled.")
                .foregroundStyle(.secondary)
            Spacer()
        }.frame(maxWidth: .infinity, alignment: .leading)
    }
}
