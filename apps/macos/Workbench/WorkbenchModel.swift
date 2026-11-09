import Combine
import Foundation

@MainActor
final class WorkbenchModel: ObservableObject {
    @Published private(set) var items: [LibraryItem] = []
    @Published private(set) var collections: [SourceCollection] = []
    @Published private(set) var topicProposals: [TopicProposal] = []
    @Published private(set) var topicOutcome = "pending"
    @Published private(set) var intakeStatus: IntakeStatus?
    @Published private(set) var assignedCollectionIds: [String] = []
    @Published private(set) var sourceMoves: [SourceMovement] = []
    @Published private(set) var selectedCollectionId = ""
    @Published var selectedMembershipId = ""

    @Published private(set) var mediaInspection: MediaInspection?
    @Published private(set) var selected: LibraryItem?
    @Published private(set) var libraryPreview = ""
    @Published private(set) var message = "Select a private catalog and watched Inbox."
    @Published private(set) var running = false
    @Published var page = 0
    @Published var noteTitle = ""
    @Published var noteText = ""
    @Published private(set) var savingNote = false
    @Published private(set) var exportingOriginal = false
    @Published private(set) var inspectingMedia = false
    private var noteCaptureId: String?

    let search: SearchWorkbenchModel
    private let worker: WorkerSession
    private var watchedInbox: URL?
    private var workerDiagnostic: String?

    init() {
        let worker = WorkerSession()
        self.worker = worker
        search = SearchWorkbenchModel(worker: worker)
    }

    func start(dataRoot: String, inbox: String, initialize: Bool, dotnetExecutable: String) {
        guard !running else { return }
        do {
            let launch = try WorkerLaunch.prepare(
                dataRoot: dataRoot, inbox: inbox, initialize: initialize,
                dotnetExecutable: dotnetExecutable
            )
            workerDiagnostic = nil
            try worker.start(
                executable: launch.executable,
                arguments: launch.arguments,
                onDiagnostic: { [weak self] message in
                    self?.workerDiagnostic = message.trimmingCharacters(in: .whitespacesAndNewlines)
                },
                onExit: { [weak self] error in self?.workerExited(error: error) }
            )
            watchedInbox = launch.inbox
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
        send(.intakeStatus, [:])
        send(.library, fields, scope: .library(selectedCollectionId, page))
        send(.collections, ["limit": 100])
        if let selected {
            refreshTopics(for: selected)
            send(.sourceMoves, ["sourceId": selected.sourceId], scope: .source(selected.sourceId))
        }
    }

    func selectCollectionFilter(_ id: String) {
        guard selectedCollectionId != id else { return }
        selectedCollectionId = id
        page = 0
        refresh()
    }

    func pauseIntake(_ paused: Bool) {
        send(.intakePause, ["paused": paused])
    }

    func retryIntakeScan() {
        send(.intakeRescan, [:])
    }

    func saveNote() {
        guard !savingNote,
              !noteText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        let id = noteCaptureId ?? UUID().uuidString.lowercased()
        noteCaptureId = id
        savingNote = send(.noteCapture, [
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
        send(.collectionCreate, ["name": name])
    }

    func renameCollection(_ id: String, name: String) {
        send(.collectionRename, ["collectionId": id, "name": name])
    }

    func archiveCollection(_ id: String) {
        send(.collectionArchive, ["collectionId": id])
    }

    func reviewTopic(_ proposal: TopicProposal, decision: String, correction: String? = nil) {
        var payload: [String: Any] = ["proposalId": proposal.proposalId,
                                      "decision": decision]
        if let correction { payload["correction"] = correction }
        send(.topicReview, payload)
    }

    func addMembership() {
        guard let source = selected, !selectedMembershipId.isEmpty else { return }
        send(.collectionAdd, ["collectionId": selectedMembershipId,
                               "sourceId": source.sourceId])
    }

    func removeMembership() {
        guard let source = selected, !selectedMembershipId.isEmpty else { return }
        send(.collectionRemove, ["collectionId": selectedMembershipId,
                                  "sourceId": source.sourceId])
    }

    func inspectSelectedMedia() {
        guard !inspectingMedia, let item = selected,
              item.sourceStatus == "active" else { return }
        guard item.isVideo else {
            message = "Select a captured video file to inspect."
            return
        }

        mediaInspection = nil
        inspectingMedia = send(.mediaInspection, [
            "sourceId": item.sourceId,
            "revision": item.revision
        ], scope: .selectedRevision(ItemRevision(itemId: item.itemId, revision: item.revision)))
    }

    func select(_ item: LibraryItem) {
        clearSelection()
        selected = item
        guard item.sourceStatus == "active" else { return }

        libraryPreview = "Loading extracted text…"
        let version = ItemRevision(itemId: item.itemId, revision: item.revision)
        loadPreview(version)
        refreshMembership(for: item)
        send(.sourceMoves, ["sourceId": item.sourceId], scope: .source(item.sourceId))
        refreshTopics(for: item)
    }

    private func loadPreview(_ version: ItemRevision) {
        if !send(.preview, ["itemId": version.itemId, "revision": version.revision],
                 scope: .selectedRevision(version)) {
            libraryPreview = "Preview unavailable."
        }
    }

    private func refreshMembership(for item: LibraryItem) {
        send(.sourceCollections, ["sourceId": item.sourceId], scope: .source(item.sourceId))
    }

    private func refreshTopics(for item: LibraryItem) {
        let version = ItemRevision(itemId: item.itemId, revision: item.revision)
        send(.topics, ["sourceId": item.sourceId], scope: .selectedRevision(version))
    }

    private func clearSelection() {
        selected = nil
        libraryPreview = ""
        assignedCollectionIds = []
        sourceMoves = []
        topicProposals = []
        topicOutcome = "pending"
        mediaInspection = nil
    }

    func exportOriginal(_ item: LibraryItem, destination: URL) {
        guard !exportingOriginal, running, item.sourceStatus == "active" else { return }
        let output = destination.standardizedFileURL.resolvingSymlinksInPath()
        if let watchedInbox, output.pathComponents.starts(with: watchedInbox.pathComponents) {
            message = "Choose a destination outside the watched Inbox to avoid capturing an exported duplicate."
            return
        }
        exportingOriginal = send(.exportOriginal, [
            "itemId": item.itemId,
            "revision": item.revision,
            "destinationPath": output.path
        ])
        if exportingOriginal {
            message = "Verifying and exporting the preserved original…"
        }
    }

    func verify() {
        if let selected { send(.verify, ["itemId": selected.itemId]) }
    }

    func retry() {
        if let selected {
            send(.retry, ["itemId": selected.itemId, "revision": selected.revision])
        }
    }

    func revoke(_ item: LibraryItem) {
        send(.revoke, ["sourceId": item.sourceId], scope: .revocation(item.sourceId))
    }

    func stop() {
        worker.stop()
    }

    private func workerExited(error: String?) {
        running = false
        watchedInbox = nil
        intakeStatus = nil
        savingNote = false
        exportingOriginal = false
        inspectingMedia = false
        items = []
        collections = []
        selectedCollectionId = ""
        selectedMembershipId = ""
        page = 0
        search.clearResults()
        clearSelection()
        if let error, let workerDiagnostic, !workerDiagnostic.isEmpty {
            message = "\(error) \(workerDiagnostic)"
        } else {
            message = error ?? "Praxis stopped."
        }
        workerDiagnostic = nil
    }

    @discardableResult
    private func send(
        _ action: WorkbenchAction, _ fields: [String: Any], scope: RequestScope = .unscoped
    ) -> Bool {
        guard running else { return false }
        do {
            try worker.send(action.rawValue, fields: fields) { [weak self] response in
                self?.apply(response, to: action, scope: scope)
            }
            return true
        } catch {
            message = error.localizedDescription
            return false
        }
    }

    private func apply(_ response: WorkerResponse, to action: WorkbenchAction, scope: RequestScope) {
        if action == .mediaInspection { inspectingMedia = false }
        guard isCurrent(scope) else { return }

        guard response.ok else {
            if action == .noteCapture { savingNote = false }
            if action == .exportOriginal { exportingOriginal = false }
            if action == .preview {
                libraryPreview = "Preview unavailable."
            }
            message = response.error ?? "The operation failed."
            return
        }

        let data = response.data
        switch action {
        case .intakeStatus, .intakePause, .intakeRescan:
            if let status = data?.status { intakeStatus = status }
            if action == .intakePause {
                message = intakeStatus?.paused == true
                    ? "New source capture paused; already queued processing may finish."
                    : "Intake resumed. Changed sources will be reconciled."
            } else if action == .intakeRescan {
                message = "Intake scan requested. Reconnect an offline Inbox before retrying."
            }
        case .noteCapture:
            savingNote = false
            discardNote()
            message = "Note saved to the private Inbox and queued for search indexing."
            page = 0
            selectedCollectionId = ""
            refresh()
        case .library:
            updateLibrary(data?.items ?? [])
        case .collections:
            collections = data?.collections ?? []
            if !selectedCollectionId.isEmpty &&
                !collections.contains(where: { $0.collectionId == selectedCollectionId }) {
                selectCollectionFilter("")
            }
        case .collectionCreate, .collectionRename, .collectionArchive:
            message = "Collection updated."
            refresh()
        case .collectionAdd, .collectionRemove:
            message = data?.changed == true
                ? "Source collections updated." : "No membership change was made."
            if let selected { refreshMembership(for: selected) }
            refresh()
        case .sourceMoves:
            sourceMoves = data?.moves ?? []
        case .sourceCollections:
            assignedCollectionIds = data?.collectionIds ?? []
        case .topics:
            topicProposals = data?.proposals ?? []
            topicOutcome = data?.outcome ?? "unavailable"
        case .topicReview:
            message = data?.reviewed == true
                ? "Review decision saved with its source revision."
                : "The proposal is unchanged or no longer current."
            if let selected { refreshTopics(for: selected) }
        case .mediaInspection:
            applyMediaInspection(data?.media)
        case .preview:
            let outcome = data?.reasonCode ?? data?.outcome ?? "pending"
            libraryPreview = data?.content ??
                "No extracted text (\(outcome)). The original remains preserved."
        case .exportOriginal:
            exportingOriginal = false
            if let receipt = data?.exported {
                message = "Verified original exported (\(receipt.length) bytes, SHA-256: \(receipt.sha256))."
            } else {
                message = "Export did not return an integrity receipt."
            }
            refresh()
        case .verify:
            message = data?.verified == true
                ? "Original integrity verified against stored SHA-256." : "Verification incomplete."
        case .retry:
            message = data?.queued == true
                ? "Processing retry queued." : "This source is not eligible for retry."
            refresh()
        case .revoke:
            if data?.revoked == true, case let .revocation(sourceId) = scope {
                if selected?.sourceId == sourceId { clearSelection() }
                search.clearResults()
                message = "Source removed from active retrieval; original bytes retained."
            } else {
                message = "This source is not active."
            }
            refresh()
        }
    }

    private func updateLibrary(_ items: [LibraryItem]) {
        self.items = items
        guard let selected else { return }
        guard let updated = items.first(where: { $0.itemId == selected.itemId }) else {
            clearSelection()
            return
        }
        if updated.revision != selected.revision ||
            updated.sha256 != selected.sha256 || updated.sourceStatus != selected.sourceStatus {
            select(updated)
        } else {
            self.selected = updated
        }
    }

    private func applyMediaInspection(_ media: MediaInspection?) {
        guard let media, let selected,
              media.sourceId == selected.sourceId,
              media.itemId == selected.itemId,
              media.revision == selected.revision,
              media.sourceSha256 == selected.sha256 else {
            message = "The selected media revision is unavailable. Refresh and inspect again."
            return
        }
        mediaInspection = media
        message = media.outcome == "header_recognized"
            ? "Verified original and recognized media header. Playback has not been validated."
            : "Original verified, but this media header is unsupported."
    }

    private func isCurrent(_ scope: RequestScope) -> Bool {
        switch scope {
        case .unscoped, .revocation:
            true
        case let .library(collectionId, offsetPage):
            collectionId == selectedCollectionId && offsetPage == page
        case let .source(sourceId):
            selected?.sourceId == sourceId && selected?.sourceStatus == "active"
        case let .selectedRevision(version):
            selected?.itemId == version.itemId && selected?.revision == version.revision &&
                selected?.sourceStatus == "active"
        }
    }

    private struct ItemRevision: Equatable {
        let itemId: String
        let revision: Int
    }

    private enum RequestScope {
        case unscoped
        case library(String, Int)
        case source(String)
        case selectedRevision(ItemRevision)
        case revocation(String)
    }
}
