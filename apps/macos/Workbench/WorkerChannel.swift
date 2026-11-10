import Foundation

/// Sends typed requests for one workbench feature and drops replies that arrive after the
/// feature moved on (a newer selection or a cleared view), so stale data never overwrites
/// current state.
@MainActor
final class WorkerChannel {
    private let worker: WorkerSession
    private var generations: [String: UUID] = [:]

    init(worker: WorkerSession) {
        self.worker = worker
    }

    /// Invalidates replies to earlier requests in `scope`.
    func reset(_ scope: String) {
        generations[scope] = UUID()
    }

    @discardableResult
    func send<Payload: Decodable>(
        _ action: String, _ fields: [String: Any] = [:], as type: Payload.Type,
        scope: String? = nil, failed: @escaping (String) -> Void,
        apply: @escaping (Payload) -> Void
    ) -> Bool {
        let generation = scope.flatMap { generations[$0] }
        do {
            try worker.send(action, fields: fields, as: type) { [weak self] reply in
                guard let self else { return }
                if let scope, self.generations[scope] != generation { return }
                if reply.ok, let data = reply.data {
                    apply(data)
                } else {
                    failed(reply.error ?? "The request failed.")
                }
            }
            return true
        } catch {
            failed(error.localizedDescription)
            return false
        }
    }
}
