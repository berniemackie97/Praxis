import Combine
import Foundation

struct SkillListItem: Decodable, Identifiable {
    let name: String
    let description: String
    let digest: String
    let revisionId: String?
    let bundled: Bool
    let enabled: Bool
    var id: String { name }
}

struct SkillRevisionItem: Decodable, Identifiable {
    let revisionId: String
    let name: String
    let description: String
    let digest: String
    let baseRevisionId: String?
    let origin: String
    let rationale: String
    let state: String
    let createdUtc: String
    var id: String { revisionId }
    var pending: Bool { state == "pending" }
}

struct SkillReview: Decodable, Identifiable {
    let summary: SkillRevisionItem
    let markdown: String
    var id: String { summary.id }
}

struct SkillPreview: Identifiable {
    let name: String
    let markdown: String
    var id: String { name }
}

private struct SkillCatalogData: Decodable { let skills: [SkillListItem] }
private struct SkillHistoryData: Decodable { let revisions: [SkillRevisionItem] }
private struct SkillTextData: Decodable { let markdown: String }

@MainActor
final class SkillsWorkbenchModel: ObservableObject {
    private let channel: WorkerChannel
    private let selectionScope = "skill-selection"
    @Published private(set) var installed: [SkillListItem] = []
    @Published private(set) var drafts: [SkillRevisionItem] = []
    @Published private(set) var history: [SkillRevisionItem] = []
    @Published private(set) var currentVersion: SkillReview?
    @Published var preview: SkillPreview?
    @Published var review: SkillReview?
    @Published private(set) var saving = false
    @Published private(set) var error: String? { didSet { if error != nil { notice = nil } } }
    @Published private(set) var notice: String? { didSet { if notice != nil { error = nil } } }

    init(channel: WorkerChannel) {
        self.channel = channel
    }

    func refresh() {
        channel.send("skillCatalog", as: SkillCatalogData.self, failed: report) { [weak self] in self?.installed = $0.skills }
        channel.send("skillHistory", ["pendingOnly": true], as: SkillHistoryData.self, failed: report) { [weak self] in
            self?.drafts = $0.revisions
        }
    }

    func open(_ skill: SkillListItem) {
        channel.reset(selectionScope)
        history = []
        channel.send("skillRead", ["name": skill.name], as: SkillTextData.self, scope: selectionScope, failed: report) { [weak self] in
            self?.preview = SkillPreview(name: skill.name, markdown: $0.markdown)
        }
        if !skill.bundled {
            channel.send("skillHistory", ["name": skill.name], as: SkillHistoryData.self, scope: selectionScope, failed: report) { [weak self] in
                self?.history = $0.revisions
            }
        }
    }

    func openRevision(_ item: SkillRevisionItem) {
        channel.reset(selectionScope)
        currentVersion = nil
        error = nil
        channel.send("skillRevisionRead", ["revisionId": item.id], as: SkillReview.self, scope: selectionScope, failed: report) { [weak self] data in
            guard let self else { return }
            preview = nil
            review = data
            if item.pending, let baseline = item.baseRevisionId {
                channel.send("skillRevisionRead", ["revisionId": baseline], as: SkillReview.self, scope: selectionScope, failed: report) { [weak self] previous in
                    if self?.review?.id == data.id { self?.currentVersion = previous }
                }
            }
        }
    }

    func importDraft(_ markdown: String, complete: @escaping () -> Void) {
        saving = true
        error = nil
        channel.send("skillDraftImport", ["markdown": markdown], as: SkillRevisionItem.self, failed: { [weak self] in
            self?.saving = false
            self?.report($0)
        }) { [weak self] draft in
            self?.saving = false
            self?.notice = "Draft imported for review."
            self?.refresh()
            complete()
            self?.openRevision(draft)
        }
    }

    func resolve(_ draft: SkillReview, accept: Bool) {
        saving = true
        error = nil
        channel.send("skillDraftResolve", ["revisionId": draft.id, "accept": accept], as: ChangedFlag.self, failed: { [weak self] in
            self?.saving = false
            self?.report($0)
        }) { [weak self] result in
            self?.saving = false
            self?.review = nil
            self?.notice = result.changed ? (accept ? "Skill version approved." : "Draft rejected.") : "That draft was already reviewed."
            self?.refresh()
        }
    }

    func setEnabled(_ skill: SkillListItem, _ enabled: Bool) {
        channel.send("skillSetEnabled", ["name": skill.name, "enabled": enabled], as: ChangedFlag.self, failed: report) { [weak self] _ in
            self?.refresh()
        }
    }

    private func report(_ message: String) { error = message }
}
