import Foundation

/// The closed vocabularies the worker accepts for entity types and relation predicates.
enum KnowledgeVocabulary {
    static let entityTypes = [
        "person", "organization", "project", "technology", "concept", "place", "document", "event", "other", "class", "function", "binary_module", "data_asset"
    ]
    static let predicates = [
        "uses", "part_of", "contains", "depends_on", "implements", "parses", "produces",
        "created_by", "owned_by", "works_on", "member_of", "located_in", "pays", "references", "related_to", "calls", "inherits_from"
    ]

    static func label(_ value: String?) -> String {
        guard let value else { return "untyped" }
        return value.replacingOccurrences(of: "_", with: " ")
    }
}

struct EntitySummary: Decodable, Identifiable, Equatable {
    let entityId: String
    let name: String
    let entityType: String?
    let typeOrigin: String
    let status: String
    let mentions: Int
    let sources: Int
    let typeConflict: Bool
    var id: String { entityId }
}

struct EntityMention: Decodable, Identifiable {
    let proposalId: String
    let sourceId: String
    let itemId: String
    let revision: Int
    let mentionType: String?
    let evidence: String
    let evidenceOffset: Int
    let status: String
    let sourceName: String
    let verdict: String?
    var id: String { proposalId }
}

struct EntityRelation: Decodable, Identifiable {
    let relationId: String
    let predicate: String
    let outgoing: Bool
    let otherEntityId: String
    let otherName: String
    let origin: String
    let evidence: String?
    var id: String { relationId }
}

struct RelatedEntity: Decodable, Identifiable {
    let entityId: String
    let name: String
    let entityType: String?
    let sharedSegments: Int
    var id: String { entityId }
}

struct EntityDetail: Decodable {
    let summary: EntitySummary
    let aliases: [String]
    let mentions: [EntityMention]
    let relations: [EntityRelation]
    let related: [RelatedEntity]
    /// Functions named as this class's members (CRadar_* for CRadar).
    let members: [RelatedEntity]?
    /// The class this function's name says it belongs to, when Praxis knows it.
    let memberOf: RelatedEntity?
}

struct EntityListData: Decodable { let entities: [EntitySummary] }
struct EntityData: Decodable { let entity: EntityDetail? }
struct ChangeData: Decodable { let changed: Bool }
struct RelationCreatedData: Decodable { let relationId: String }

/// A change Praxis made to, or proposes for, its own classification of an entity.
struct KnowledgeRevision: Decodable, Identifiable, Equatable {
    let revisionId: Int
    let entityId: String
    let entityName: String
    let field: String
    let relationId: String?
    let before: String?
    let after: String
    let afterLabel: String
    let trigger: String
    let basis: String
    let state: String
    let createdUtc: String
    let decidedUtc: String?
    var id: Int { revisionId }
}

struct KnowledgeRevisionsData: Decodable { let revisions: [KnowledgeRevision] }

/// The knowledge revision (moves when anything the graph can show changes) and newest revision log id.
struct KnowledgeWatermarkData: Decodable {
    let graphRevision: Int64
    let latestRevisionId: Int64
}
