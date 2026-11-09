import AppKit
import Combine
import Foundation

@MainActor
final class Workbench: ObservableObject {
    @Published private(set) var items: [LibraryItem] = []
    @Published private(set) var collections: [SourceCollection] = []
    @Published private(set) var topicProposals: [TopicProposal] = []
    @Published private(set) var topicOutcome = "pending"
    @Published private(set) var intakeStatus: IntakeStatus?
    @Published private(set) var assignedCollectionIds: [String] = []
    @Published private(set) var sourceMoves: [SourceMovement] = []
    @Published var selectedCollectionId = ""
    @Published var selectedMembershipId = ""

    @Published private(set) var hits: [SearchHit] = []
    @Published private(set) var videoClips: [VideoCue] = []
    @Published private(set) var mediaInspection: MediaInspection?
    @Published private(set) var budgetReport: MonthlyBudgetReport?
    @Published private(set) var budgetScenario: BudgetScenario?
    @Published var budgetTargetText: [String: String] = [:]
    @Published var budgetYear = Calendar.current.component(.year, from: Date())
    @Published var budgetMonth = Calendar.current.component(.month, from: Date())
    @Published private(set) var selected: LibraryItem?
    @Published private(set) var preview = ""
    @Published private(set) var message = "Select a private catalog and watched Inbox."
    @Published private(set) var running = false
    @Published var query = ""
    @Published var page = 0
    @Published var noteTitle = ""
    @Published var noteText = ""
    @Published private(set) var savingNote = false
    @Published private(set) var exportingOriginal = false
    @Published private(set) var inspectingMedia = false
    private var noteCaptureId: String?
    private var budgetTargetRevision = 0

    private var child: Process?
    private var input: FileHandle?
    private var buffer = Data()
    private var requests: [String: String] = [:]
    private var previewTarget = ""
    private var watchedInboxPath = ""

    func start(dataRoot: String, inbox: String, initialize: Bool, dotnetExecutable: String) {
        guard !running else { return }
        let data = URL(fileURLWithPath: dataRoot).standardizedFileURL.resolvingSymlinksInPath()
        let source = URL(fileURLWithPath: inbox).standardizedFileURL.resolvingSymlinksInPath()
        guard data.path != source.path,
              !data.path.hasPrefix(source.path + "/"),
              !source.path.hasPrefix(data.path + "/") else {
            message = "Catalog and watched Inbox must not overlap."
            return
        }
        if initialize && !FileManager.default.fileExists(atPath: source.path) {
            message = "Choose an available Inbox for first-time setup."
            return
        }
        do {
            if initialize && !FileManager.default.fileExists(atPath: data.path) {
                try FileManager.default.createDirectory(at: data,
                    withIntermediateDirectories: false,
                    attributes: [.posixPermissions: 0o700])
            }
            guard FileManager.default.fileExists(atPath: data.path) else {
                message = "The configured catalog is missing. Reconnect or restore it."
                return
            }
            guard let worker = Bundle.main.resourceURL?
                .appendingPathComponent("worker/Praxis.Worker.dll"),
                FileManager.default.fileExists(atPath: worker.path) else {
                message = "Praxis.Worker.dll is missing from the app bundle."
                return
            }
            let candidates = [
                dotnetExecutable.isEmpty ? nil : dotnetExecutable,
                ProcessInfo.processInfo.environment["DOTNET_ROOT"].map { $0 + "/dotnet" },
                FileManager.default.homeDirectoryForCurrentUser
                    .appendingPathComponent(".dotnet/dotnet").path,
                "/usr/local/share/dotnet/dotnet",
                "/opt/homebrew/bin/dotnet",
                "/usr/local/bin/dotnet"
            ].compactMap { $0 }
            guard let executable = candidates.first(where: {
                FileManager.default.isExecutableFile(atPath: $0)
            }) else {
                message = "Install the .NET 10 runtime and reopen Praxis."
                return
            }

            let process = Process()
            process.executableURL = URL(fileURLWithPath: executable)
            process.arguments = [worker.path, "--data-root", data.path,
                                 "--intake", source.path, "--stdio"]
            let stdin = Pipe(), stdout = Pipe(), stderr = Pipe()
            process.standardInput = stdin
            process.standardOutput = stdout
            process.standardError = stderr
            stdout.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let bytes = handle.availableData
                if bytes.isEmpty {
                    handle.readabilityHandler = nil
                    return
                }
                DispatchQueue.main.async { self?.receive(bytes) }
            }
            stderr.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let bytes = handle.availableData
                if bytes.isEmpty {
                    handle.readabilityHandler = nil
                    return
                }
                DispatchQueue.main.async {
                    self?.message = String(decoding: bytes.prefix(2048), as: UTF8.self)
                }
            }
            process.terminationHandler = { [weak self] process in
                DispatchQueue.main.async {
                    self?.running = false
                    self?.intakeStatus = nil
                    self?.savingNote = false
                    self?.exportingOriginal = false
                    self?.mediaInspection = nil
                    self?.inspectingMedia = false
                    self?.requests.removeAll()
                    self?.buffer.removeAll()
                    if process.terminationStatus != 0 {
                        self?.message = "The worker exited (\(process.terminationStatus)). Check the private catalog."
                    }
                }
            }
            try process.run()
            child = process
            watchedInboxPath = source.path
            input = stdin.fileHandleForWriting
            running = true
            message = "Praxis started. Checking the approved Inbox; preserved sources remain available if it is offline."
            refresh()
        } catch {
            message = "Praxis could not start: \(error.localizedDescription)"
        }
    }

    func refresh() {
        guard running, !exportingOriginal else { return }
        var fields: [String: Any] = ["limit": 100, "offset": page * 100]
        if !selectedCollectionId.isEmpty {
            fields["collectionId"] = selectedCollectionId
        }
        send("intakeStatus", [:])
        send("library", fields)
        send("collections", ["limit": 100])
        if let selected {
            send("topics", ["sourceId": selected.sourceId])
            send("sourceMoves", ["sourceId": selected.sourceId])
        }
    }

    func pauseIntake(_ paused: Bool) {
        send("intakePause", ["paused": paused])
    }

    func retryIntakeScan() {
        send("intakeRescan", [:])
    }

    func saveNote() {
        guard !savingNote,
              !noteText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        let id = noteCaptureId ?? UUID().uuidString.lowercased()
        noteCaptureId = id
        savingNote = send("noteCapture", [
            "captureId": id,
            "title": noteTitle,
            "text": noteText
        ])
    }

    func discardNote() {
        guard !savingNote else { return }
        noteCaptureId = nil
        noteTitle = ""
        noteText = ""
    }

    func createCollection(_ name: String) {
        send("collectionCreate", ["name": name])
    }

    func renameCollection(_ id: String, name: String) {
        send("collectionRename", ["collectionId": id, "name": name])
    }

    func archiveCollection(_ id: String) {
        send("collectionArchive", ["collectionId": id])
    }

    func reviewTopic(_ proposal: TopicProposal, decision: String, correction: String? = nil) {
        var payload: [String: Any] = ["proposalId": proposal.proposalId,
                                      "decision": decision]
        if let correction { payload["correction"] = correction }
        send("topicReview", payload)
    }

    func addMembership() {
        guard let source = selected, !selectedMembershipId.isEmpty else { return }
        send("collectionAdd", ["collectionId": selectedMembershipId,
                               "sourceId": source.sourceId])
    }

    func removeMembership() {
        guard let source = selected, !selectedMembershipId.isEmpty else { return }
        send("collectionRemove", ["collectionId": selectedMembershipId,
                                  "sourceId": source.sourceId])
    }


    func search() {
        if !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            send("search", ["query": query])
            send("mediaCues", ["query": query, "limit": 20])
        } else {
            hits = []
            videoClips = []
        }
    }

    func inspectSelectedMedia() {
        guard !inspectingMedia, let item = selected,
              item.sourceStatus == "active" else { return }
        let ext = URL(fileURLWithPath: item.sourceLocator).pathExtension.lowercased()
        guard ["mp4", "mov", "m4v", "webm", "mkv"].contains(ext) else {
            message = "Select a captured video file to inspect."
            return
        }

        mediaInspection = nil
        inspectingMedia = send("mediaInspection", [
            "sourceId": item.sourceId,
            "revision": item.revision
        ])
    }

    func analyzeSelectedBudget() {
        guard let item = selected, item.sourceStatus == "active",
              URL(fileURLWithPath: item.sourceLocator).pathExtension.lowercased() == "csv"
        else {
            message = "Select an active financial CSV in the Library first."
            return
        }

        clearBudgetResults()
        send("budgetSummary", [
            "sourceId": item.sourceId,
            "year": budgetYear,
            "month": budgetMonth
        ])
    }

    func clearBudgetResults() {
        budgetTargetRevision += 1
        budgetReport = nil
        budgetScenario = nil
        budgetTargetText.removeAll()
    }

    func updateBudgetTarget(category: String, text: String) {
        budgetTargetRevision += 1
        budgetTargetText[category] = text
        budgetScenario = nil
    }

    func previewBudgetScenario() {
        guard let report = budgetReport,
              let selected, selected.itemId == report.itemId,
              selected.revision == report.revision,
              selected.sha256 == report.sourceSha256,
              selected.sourceStatus == "active" else {
            message = "Reanalyze the current active CSV before previewing a budget."
            return
        }

        let targets = budgetTargetText
            .filter { !$0.value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .map { ["category": $0.key, "target": $0.value] }
        budgetScenario = nil
        send("budgetScenario", [
            "sourceId": report.sourceId,
            "year": report.year,
            "month": report.month,
            "targets": targets
        ])
    }

    func select(_ item: LibraryItem) {
        clearBudgetResults()
        mediaInspection = nil
        selected = item
        preview = "Loading extracted text…"
        previewTarget = "\(item.itemId):\(item.revision)"
        assignedCollectionIds = []
        sourceMoves = []
        topicProposals = []
        topicOutcome = "pending"
        send("preview", ["itemId": item.itemId, "revision": item.revision])
        send("sourceCollections", ["sourceId": item.sourceId])
        send("sourceMoves", ["sourceId": item.sourceId])
        send("topics", ["sourceId": item.sourceId])
    }

    func previewHit(_ hit: SearchHit) {
        preview = "Loading extracted text…"
        previewTarget = "\(hit.itemId):\(hit.revision)"
        send("preview", ["itemId": hit.itemId, "revision": hit.revision])
    }

    func exportOriginal(_ item: LibraryItem, destination: URL) {
        guard !exportingOriginal, running, item.sourceStatus == "active" else { return }
        let path = destination.standardizedFileURL.resolvingSymlinksInPath().path
        if path.hasPrefix(watchedInboxPath + "/") {
            message = "Choose a destination outside the watched Inbox to avoid capturing an exported duplicate."
            return
        }
        exportingOriginal = send("exportOriginal", [
            "itemId": item.itemId,
            "revision": item.revision,
            "destinationPath": path
        ])
        if exportingOriginal {
            message = "Verifying and exporting the preserved original…"
        }
    }

    func verify() {
        if let selected { send("verify", ["itemId": selected.itemId]) }
    }

    func retry() {
        if let selected {
            send("retry", ["itemId": selected.itemId, "revision": selected.revision])
        }
    }

    func revoke() {
        if let selected { send("revoke", ["sourceId": selected.sourceId]) }
    }

    func stop() {
        guard running else { return }
        send("stop", [:])
        input?.closeFile()
        input = nil
    }

    @discardableResult
    private func send(_ action: String, _ fields: [String: Any]) -> Bool {
        guard let input, running else { return false }
        guard requests.count < 64 else {
            message = "The worker is busy. Retry after pending requests complete."
            return false
        }
        let id = UUID().uuidString
        var request = fields
        request["requestId"] = id
        request["action"] = action
        guard let encoded = try? JSONSerialization.data(withJSONObject: request),
              encoded.count < 16_000 else {
            message = "The request exceeds the private protocol limit."
            return false
        }
        do {
            if action == "preview", let itemId = fields["itemId"] as? String,
               let revision = fields["revision"] as? Int {
                requests[id] = "preview:\(itemId):\(revision)"
            } else if action == "topics", let sourceId = fields["sourceId"] as? String {
                requests[id] = "topics:\(sourceId)"
            } else if action == "sourceMoves",
                      let sourceId = fields["sourceId"] as? String {
                requests[id] = "sourceMoves:\(sourceId)"
            } else if action == "sourceCollections",
                      let sourceId = fields["sourceId"] as? String {
                requests[id] = "sourceCollections:\(sourceId)"
            } else if action == "library" {
                requests[id] = "library:\(selectedCollectionId):\(page)"
            } else if action == "mediaInspection",
                      let sourceId = fields["sourceId"] as? String,
                      let revision = fields["revision"] as? Int {
                requests[id] = "mediaInspection:\(sourceId):\(revision)"
            } else if action == "budgetSummary" || action == "budgetScenario",
                      let sourceId = fields["sourceId"] as? String,
                      let year = fields["year"] as? Int,
                      let month = fields["month"] as? Int {
                let suffix = action == "budgetScenario" ? ":\(budgetTargetRevision)" : ""
                requests[id] = "\(action):\(sourceId):\(year):\(month)\(suffix)"
            } else {
                requests[id] = action
            }
            try input.write(contentsOf: encoded + Data([0x0A]))
            return true
        } catch {
            requests.removeValue(forKey: id)
            message = "The worker is no longer accepting requests."
            return false
        }
    }

    private func receive(_ bytes: Data) {
        guard !bytes.isEmpty else { return }
        buffer.append(bytes)
        if buffer.count > 2_000_000 {
            buffer.removeAll()
            message = "A response exceeded the UI memory budget."
            return
        }
        while let end = buffer.firstIndex(of: 0x0A) {
            let line = Data(buffer[..<end])
            buffer.removeSubrange(...end)
            guard let reply = try? JSONDecoder().decode(Response.self, from: line),
                  let id = reply.requestId,
                  let action = requests.removeValue(forKey: id) else { continue }
            guard reply.ok else {
                if action == "noteCapture" { savingNote = false }
                if action == "exportOriginal" { exportingOriginal = false }
                if action.hasPrefix("mediaInspection:") {
                    inspectingMedia = false
                    guard let selected,
                          action == "mediaInspection:\(selected.sourceId):\(selected.revision)" else {
                        continue
                    }
                    mediaInspection = nil
                }
                if action.hasPrefix("budgetSummary:") {
                    clearBudgetResults()
                } else if action.hasPrefix("budgetScenario:") {
                    guard let report = budgetReport,
                          action == "budgetScenario:\(report.sourceId):\(report.year):\(report.month):\(budgetTargetRevision)" else { continue }
                    budgetScenario = nil
                }
                message = reply.error ?? "The operation failed."
                continue
            }
            switch action {
            case "intakeStatus", "intakePause", "intakeRescan":
                if let status = reply.data?.status { intakeStatus = status }
                if action == "intakePause" {
                    message = intakeStatus?.paused == true
                        ? "New source capture paused; already queued processing may finish."
                        : "Intake resumed. Changed sources will be reconciled."
                } else if action == "intakeRescan" {
                    message = "Intake scan requested. Reconnect an offline Inbox before retrying."
                }
            case "noteCapture":
                savingNote = false
                noteCaptureId = nil
                noteTitle = ""
                noteText = ""
                message = "Note saved to the private Inbox and queued for search indexing."
                page = 0
                selectedCollectionId = ""
                refresh()
            case let context where context.hasPrefix("library:"):
                guard context == "library:\(selectedCollectionId):\(page)" else { continue }
                items = reply.data?.items ?? []
                if let previous = selected {
                    if let updated = items.first(where: { $0.itemId == previous.itemId }) {
                        if updated.revision != previous.revision ||
                            updated.sha256 != previous.sha256 ||
                            updated.sourceStatus != "active" {
                            clearBudgetResults()
                            mediaInspection = nil
                        }
                        selected = updated
                    } else {
                        clearBudgetResults()
                        mediaInspection = nil
                        selected = nil
                    }
                }
            case "collections":
                collections = reply.data?.collections ?? []
                if !selectedCollectionId.isEmpty &&
                    !collections.contains(where: { $0.collectionId == selectedCollectionId }) {
                    selectedCollectionId = ""
                    page = 0
                    refresh()
                }
            case "collectionCreate", "collectionRename", "collectionArchive":
                message = "Collection updated."
                refresh()
            case "collectionAdd", "collectionRemove":
                message = reply.data?.changed == true
                    ? "Source collections updated." : "No membership change was made."
                if let source = selected {
                    send("sourceCollections", ["sourceId": source.sourceId])
                }
                refresh()
            case let context where context.hasPrefix("sourceMoves:"):
                if let selected, context == "sourceMoves:\(selected.sourceId)" {
                    sourceMoves = reply.data?.moves ?? []
                }
            case let context where context.hasPrefix("sourceCollections:"):
                if let selected, context == "sourceCollections:\(selected.sourceId)" {
                    assignedCollectionIds = reply.data?.collectionIds ?? []
                }
            case let context where context.hasPrefix("topics:"):
                if let selected, context == "topics:\(selected.sourceId)" {
                    topicProposals = reply.data?.proposals ?? []
                    topicOutcome = reply.data?.outcome ?? "unavailable"
                }
            case "topicReview":
                message = reply.data?.reviewed == true
                    ? "Review decision saved with its source revision."
                    : "The proposal is unchanged or no longer current."
                if let selected { send("topics", ["sourceId": selected.sourceId]) }
            case "search": hits = reply.data?.hits ?? []
            case "mediaCues": videoClips = reply.data?.clips ?? []
            case let context where context.hasPrefix("mediaInspection:"):
                inspectingMedia = false
                guard let selected,
                      context == "mediaInspection:\(selected.sourceId):\(selected.revision)",
                      selected.sourceStatus == "active",
                      let media = reply.data?.media,
                      media.sourceId == selected.sourceId,
                      media.itemId == selected.itemId,
                      media.revision == selected.revision,
                      media.sourceSha256 == selected.sha256 else { continue }
                mediaInspection = media
                message = media.outcome == "header_recognized"
                    ? "Verified original and recognized media header. Playback has not been validated."
                    : "Original verified, but this media header is unsupported."
            case let context where context.hasPrefix("budgetSummary:"):
                guard let selected,
                      context == "budgetSummary:\(selected.sourceId):\(budgetYear):\(budgetMonth)",
                      selected.sourceStatus == "active" else { continue }
                if let report = reply.data?.budget,
                   report.revision == selected.revision &&
                   report.sourceSha256 == selected.sha256 {
                    budgetReport = report
                } else {
                    budgetReport = nil
                }
                budgetScenario = nil
                budgetTargetText.removeAll()
                message = budgetReport == nil
                    ? "No budget report was returned."
                    : "Posted amounts are actual. Category targets are hypothetical and do not change transactions."
            case let context where context.hasPrefix("budgetScenario:"):
                guard let report = budgetReport,
                      context == "budgetScenario:\(report.sourceId):\(report.year):\(report.month):\(budgetTargetRevision)",
                      let selected, selected.revision == report.revision,
                      selected.sha256 == report.sourceSha256 else { continue }
                if let scenario = reply.data?.scenario,
                   scenario.revision == report.revision &&
                   scenario.sourceSha256 == report.sourceSha256 {
                    budgetScenario = scenario
                } else {
                    budgetScenario = nil
                }
                message = budgetScenario == nil
                    ? "No scenario result was returned."
                    : "Illustrative comparison calculated locally; no expenses were changed."
            case let context where context.hasPrefix("preview:"):
                if context == "preview:\(previewTarget)" {
                    preview = reply.data?.content ??
                        "No extracted text (\(reply.data?.reasonCode ?? reply.data?.outcome ?? "pending")). The original remains preserved."
                }
            case "exportOriginal":
                exportingOriginal = false
                if let receipt = reply.data?.exported {
                    message = "Verified original exported (\(receipt.length) bytes, SHA-256: \(receipt.sha256))."
                } else {
                    message = "Export did not return an integrity receipt."
                }
                refresh()
            case "verify":
                message = reply.data?.verified == true
                    ? "Original integrity verified against stored SHA-256." : "Verification incomplete."
            case "retry":
                message = reply.data?.queued == true
                    ? "Processing retry queued." : "This source is not eligible for retry."
                refresh()
            case "revoke":
                if reply.data?.revoked == true {
                    clearBudgetResults()
                    mediaInspection = nil
                    selected = nil
                }
                message = reply.data?.revoked == true
                    ? "Source removed from active retrieval; original bytes retained."
                    : "This source is not active."
                refresh()
            default: break
            }
        }
    }
}

