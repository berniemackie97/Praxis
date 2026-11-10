import Combine
import Foundation

@MainActor
final class SearchWorkbenchModel: ObservableObject {
    private let worker: WorkerSession
    private var generation = UUID()
    private var selectedHitId: String?

    @Published var query = ""
    @Published private(set) var hits: [SearchHit] = []
    @Published private(set) var videoClips: [VideoCue] = []
    @Published private(set) var preview = ""
    @Published private(set) var error: String?

    init(worker: WorkerSession) {
        self.worker = worker
    }

    func submit() {
        clearResults()
        let term = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !term.isEmpty else { return }

        request("search", ["query": term]) { model, response in
            model.hits = response.data?.hits ?? []
        }
        request("mediaCues", ["query": term, "limit": 20]) { model, response in
            model.videoClips = response.data?.clips ?? []
        }
    }

    func select(_ hit: SearchHit) {
        selectedHitId = hit.id
        preview = "Loading extracted text…"
        error = nil
        let sent = request("preview", ["itemId": hit.itemId, "revision": hit.revision],
                           forHit: hit.id) { model, response in
            guard response.ok else {
                model.preview = "Preview unavailable."
                return
            }

            let data = response.data
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
