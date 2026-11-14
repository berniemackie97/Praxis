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
        .map { entry in
            let first = entry.value[0]
            return EntityRelationGroup(id: entry.key, predicate: first.predicate,
                                       outgoing: first.outgoing, otherEntityId: first.otherEntityId,
                                       otherName: first.otherName,
                                       observations: entry.value.sorted { $0.relationId < $1.relationId })
        }
        .sorted { left, right in
            if left.otherName != right.otherName { return left.otherName < right.otherName }
            if left.predicate != right.predicate { return left.predicate < right.predicate }
            return left.outgoing && !right.outgoing
        }
    }
}

/// The relationship groups of one predicate and direction, such as "Calls" or "Inherited by".
/// Structural relations come first; long lists such as call graphs start collapsed.
struct EntityRelationSection: Identifiable {
    let id: String
    let title: String
    let groups: [EntityRelationGroup]

    /// Lists longer than this show their first rows and a "Show all" control.
    static let collapsedRows = 6

    private static let order = [
        "inherits_from", "implements", "part_of", "member_of", "contains", "depends_on", "uses", "produces", "parses",
        "references", "owned_by", "created_by", "works_on", "located_in", "pays", "related_to", "calls"
    ]

    static func sections(_ groups: [EntityRelationGroup]) -> [EntityRelationSection] {
        Dictionary(grouping: groups) { "\($0.outgoing ? "out" : "in")|\($0.predicate)" }
            .map { key, members in
                let first = members[0]
                return EntityRelationSection(id: key, title: title(first.predicate, outgoing: first.outgoing),
                                             groups: members.sorted { $0.otherName < $1.otherName })
            }
            .sorted { left, right in
                let l = rank(left), r = rank(right)
                return l != r ? l < r : left.id < right.id
            }
    }

    private static func rank(_ section: EntityRelationSection) -> Int {
        let predicate = section.groups[0].predicate
        return (order.firstIndex(of: predicate) ?? order.count - 1) * 2 + (section.groups[0].outgoing ? 0 : 1)
    }

    static func title(_ predicate: String, outgoing: Bool) -> String {
        if outgoing { return KnowledgeVocabulary.label(predicate).capitalizedFirst }
        switch predicate {
        case "calls": return "Called by"
        case "part_of", "member_of": return "Has members"
        case "contains": return "Contained in"
        case "inherits_from": return "Inherited by"
        case "implements": return "Implemented by"
        case "uses": return "Used by"
        case "depends_on": return "Depended on by"
        case "references": return "Referenced by"
        case "produces": return "Produced by"
        case "parses": return "Parsed by"
        case "created_by": return "Created"
        case "owned_by": return "Owns"
        case "works_on": return "Worked on by"
        case "located_in": return "Location of"
        case "pays": return "Paid by"
        default: return KnowledgeVocabulary.label(predicate).capitalizedFirst + " (from)"
        }
    }
}

private extension String {
    var capitalizedFirst: String { prefix(1).uppercased() + dropFirst() }
}
