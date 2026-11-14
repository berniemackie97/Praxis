import Foundation

/// Groups a displayed assertion without merging its separate evidence records or stable IDs.
struct EntityRelationGroup: Identifiable {
    let id: String
    let predicate: String
    let outgoing: Bool
    let otherEntityId: String
    let otherName: String
    let observations: [EntityRelation]

    static func group(_ relations: [EntityRelation]) -> [EntityRelationGroup] {
        Dictionary(grouping: relations) {
            "\($0.outgoing ? "out" : "in")|\($0.predicate)|\($0.otherEntityId)"
        }
        .map { id, observations in
            let first = observations[0]
            return EntityRelationGroup(id: id, predicate: first.predicate,
                                       outgoing: first.outgoing, otherEntityId: first.otherEntityId,
                                       otherName: first.otherName,
                                       observations: observations.sorted { $0.relationId < $1.relationId })
        }
        .sorted { left, right in
            if left.otherName != right.otherName { return left.otherName < right.otherName }
            if left.predicate != right.predicate { return left.predicate < right.predicate }
            return left.outgoing && !right.outgoing
        }
    }
}
