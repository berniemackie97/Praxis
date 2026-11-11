import Combine
import Foundation

@MainActor
final class SearchWorkbenchModel: ObservableObject {
    private let worker: WorkerSession
    private var generation = UUID()
    private var selectedHitId: String?

    @Published var query = ""
    @Published var selectedCollectionId: String? = nil {
        didSet {
            if selectedCollectionId != oldValue { clearResults() }
        }
    }
    @Published private(set) var collections: [SourceCollection] = []
    @Published private(set) var hits: [SearchHit] = []
    @Published private(set) var videoClips: [VideoCue] = []
    @Published private(set) var preview = ""
    @Published private(set) var mode = "Keyword search"
    @Published private(set) var notice: String?
    @Published private(set) var indexing = false
    @Published private(set) var error: String?

    init(worker: WorkerSession) {
        self.worker = worker
    }

    func loadCollections() {
        request("collections", ["limit": 200]) { model, response in
            guard response.ok else { return }
            model.collections = response.data?.collections ?? []
            if let selected = model.selectedCollectionId,
               !model.collections.contains(where: { $0.collectionId == selected }) {
                model.selectedCollectionId = nil
            }
        }
    }

    func submit() {
        clearResults()
        let term = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !term.isEmpty else { return }

        var fields: [String: Any] = ["query": term]
        if let selectedCollectionId { fields["collectionId"] = selectedCollectionId }
        request("search", fields) { model, response in
            model.hits = response.data?.hits ?? []
            model.mode = response.data?.mode == "hybrid" ? "Hybrid search" : "Keyword search"
            model.notice = response.data?.notice
        }
        // Media cue queries do not yet accept collection scopes. Never mix unscoped
        // caption results into a source-restricted search.
        if selectedCollectionId == nil {
            request("mediaCues", ["query": term, "limit": 20]) { model, response in
                model.videoClips = response.data?.clips ?? []
            }
        }
    }

    func updateSemanticIndex(rebuild: Bool = false) {
        guard !indexing else { return }
        indexing = true
        do {
            try worker.send("semanticIndex", fields: ["rebuild": rebuild]) { [weak self] response in
                guard let self else { return }
                self.indexing = false
                guard response.ok else {
                    self.error = response.error ?? "Semantic indexing failed."
                    return
                }
                if let update = response.data?.index {
                    self.notice = "Indexed \(update.indexed) segments. \(update.hasMore ? "More remain; update again." : "Index is current.")"
                }
            }
        } catch {
            indexing = false
            self.error = error.localizedDescription
        }
    }

    func select(_ hit: SearchHit) {
        selectedHitId = hit.id
        preview = "Loading extracted text…"
        error = nil
        var fields: [String: Any] = [
            "itemId": hit.itemId, "revision": hit.revision,
            "sourceId": hit.sourceId, "originalSha256": hit.originalSha256
        ]
        if let selectedCollectionId { fields["collectionId"] = selectedCollectionId }
        let sent = request("preview", fields, forHit: hit.id) { model, response in
            guard response.ok else {
                model.preview = "Preview unavailable."
                return
            }

            let data = response.data
            if data?.outcome == "unavailable" {
                model.hits.removeAll(where: { $0.id == hit.id })
                model.preview = "This result is no longer current or in the selected collection."
                return
            }
            let outcome = data?.reasonCode ?? data?.outcome ?? "pending"
            model.preview = data?.content ??
                "No extracted text (\(outcome)). The original remains preserved."
        }
        if !sent { preview = "Preview unavailable." }
    }

    func isPreviewing(_ hit: SearchHit) -> Bool {
        selectedHitId == hit.id
    }

    func clearResults() {
        generation = UUID()
        selectedHitId = nil
        hits = []
        videoClips = []
        preview = ""
        notice = nil
        error = nil
    }

    @discardableResult
    private func request(
        _ action: String, _ fields: [String: Any], forHit hitId: String? = nil,
        apply: @escaping (SearchWorkbenchModel, WorkerResponse) -> Void
    ) -> Bool {
        let generation = self.generation
        do {
            try worker.send(action, fields: fields) { [weak self] response in
                guard let self, self.generation == generation else { return }
                if let hitId, self.selectedHitId != hitId { return }
                if !response.ok { self.error = response.error ?? "The request failed." }
                apply(self, response)
            }
            return true
        } catch {
            self.error = error.localizedDescription
            return false
        }
    }
}
