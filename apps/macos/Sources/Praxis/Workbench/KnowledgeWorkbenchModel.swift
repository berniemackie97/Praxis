import Combine
import Foundation

/// Entities across all sources and the owner's decisions about them.
@MainActor
final class KnowledgeWorkbenchModel: ObservableObject {
    private static let detailScope = "detail"
    private let channel: WorkerChannel
    let graph: GraphWorkbenchModel

    @Published var search = ""
    @Published var typeFilter = ""
    /// Also list names only the local model has guessed (unconfirmed).
    @Published var includeSuggestions = false
    @Published private(set) var entities: [EntitySummary] = []
    @Published private(set) var selected: EntityDetail?
    /// Corrections Praxis applied to its own classifications and the ones it proposes, newest first.
    @Published private(set) var revisions: [KnowledgeRevision] = []
    /// The latest failure or confirmation; setting one clears the other.
    @Published private(set) var error: String? { didSet { if error != nil { notice = nil } } }
    @Published private(set) var notice: String? { didSet { if notice != nil { error = nil } } }

    init(channel: WorkerChannel) {
        self.channel = channel
        graph = GraphWorkbenchModel(channel: channel)
    }

    func refresh() {
        var fields: [String: Any] = ["limit": 200]
        let term = search.trimmingCharacters(in: .whitespacesAndNewlines)
        if !term.isEmpty { fields["search"] = String(term.prefix(120)) }
        if !typeFilter.isEmpty { fields["entityType"] = typeFilter }
        if includeSuggestions { fields["includeSuggestions"] = true }
        channel.send("entities", fields, as: EntityListData.self, scope: "list", failed: report) { [weak self] data in
            self?.entities = data.entities
        }
        if let selected { load(selected.summary.entityId) }
    }

    func setIncludeSuggestions(_ include: Bool) {
        guard includeSuggestions != include else { return }
        includeSuggestions = include
        refresh()
    }

    func setTypeFilter(_ filter: String) {
        guard typeFilter != filter else { return }
        typeFilter = filter
        refresh()
    }

    func select(_ entity: EntitySummary) {
        selectEntityId(entity.entityId)
    }

    func selectEntityId(_ id: String) {
        selected = nil
        load(id)
    }

    func rename(to name: String) { decide("entityRename", ["name": name], "Renamed.") }
    func setType(_ type: String) { decide("entitySetType", ["entityType": type], "Type set by you.") }
    func reject() { decide("entityReject", [:], "Rejected. It stays hidden and the local model is told not to report it.") }
    func restore() { decide("entityRestore", [:], "Restored.") }

    func merge(into target: EntitySummary) {
        decide("entityMerge", ["targetEntityId": target.entityId], "Merged into \(target.name).") { [weak self] in
            self?.select(target)
        }
    }

    func relate(_ predicate: String, to target: EntitySummary) {
        guard let entity = selected?.summary else { return }
        channel.send("entityRelate",
                     ["entityId": entity.entityId, "predicate": predicate, "targetEntityId": target.entityId],
                     as: RelationCreatedData.self, failed: report) { [weak self] _ in
            self?.notice = "Added: \(entity.name) \(KnowledgeVocabulary.label(predicate)) \(target.name)."
            self?.refresh()
        }
    }

    func refreshRevisions() {
        channel.send("knowledgeRevisions", ["limit": 200], as: KnowledgeRevisionsData.self,
                     scope: "revisions", failed: report) { [weak self] data in
            self?.revisions = data.revisions
        }
    }

    /// Accepting applies the proposal as your own type decision; dismissing means it is never proposed again.
    func decide(_ revision: KnowledgeRevision, accept: Bool) {
        channel.send("knowledgeRevisionDecide",
                     ["revisionId": revision.revisionId, "decision": accept ? "accepted" : "dismissed"],
                     as: ChangeData.self, failed: report) { [weak self] data in
            guard let self else { return }
            notice = !data.changed ? "That proposal was already decided."
                : accept ? "\(revision.entityName) is now \(KnowledgeVocabulary.label(revision.after)). Similar entities will be checked."
                : "Dismissed. Praxis will not propose it again."
            refreshRevisions()
            refresh()
        }
    }

    private func load(_ entityId: String) {
        channel.reset(Self.detailScope)
        channel.send("entity", ["entityId": entityId], as: EntityData.self,
                     scope: Self.detailScope, failed: report) { [weak self] data in
            self?.selected = data.entity
        }
    }

    private func decide(
        _ action: String, _ fields: [String: Any], _ success: String, then: (() -> Void)? = nil
    ) {
        guard let entity = selected?.summary else { return }
        var request = fields
        request["entityId"] = entity.entityId
        channel.send(action, request, as: ChangeData.self, failed: report) { [weak self] data in
            guard let self else { return }
            notice = data.changed ? success : "No change: the entity was already in that state or was merged."
            if let then { then() } else { refresh() }
        }
    }

    private func report(_ message: String) {
        error = message
    }
}
