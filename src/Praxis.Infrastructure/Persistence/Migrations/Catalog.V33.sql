-- Knowledge revision counter for graph cursors and live views, and merge/relation revisions (#463).

-- One counter that moves only when something the graph can show changes. Graph pages carry it in
-- their cursors (instead of the connection's total_changes, which moved on every catalog write and
-- invalidated open pages while intake ran) and clients poll it to refresh what they display.
CREATE TABLE graph_revision (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    revision INTEGER NOT NULL CHECK (revision >= 0)
) STRICT;
INSERT INTO graph_revision (singleton, revision) VALUES (1, 1);

CREATE TRIGGER graph_revision_mention_insert AFTER INSERT ON entity_mentions BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_mention_update AFTER UPDATE ON entity_mentions BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_mention_delete AFTER DELETE ON entity_mentions BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_relation_insert AFTER INSERT ON knowledge_relations BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_relation_update AFTER UPDATE ON knowledge_relations BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_relation_delete AFTER DELETE ON knowledge_relations BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_entity_insert AFTER INSERT ON knowledge_entities BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_entity_update AFTER UPDATE ON knowledge_entities BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_decision_insert AFTER INSERT ON entity_decisions BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_proposal_insert AFTER INSERT ON enrichment_proposals BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_proposal_update AFTER UPDATE ON enrichment_proposals BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_run_insert AFTER INSERT ON enrichment_runs BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_verification_item AFTER UPDATE OF verdict ON verification_items BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_verification_request AFTER UPDATE OF status, completed_utc ON verification_requests BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_source_status AFTER UPDATE OF status ON sources BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_membership_insert AFTER INSERT ON source_collection_memberships BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_membership_delete AFTER DELETE ON source_collection_memberships BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_collection_update AFTER UPDATE ON source_collections BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_intake_document_insert AFTER INSERT ON intake_documents BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_intake_document_update AFTER UPDATE ON intake_documents BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_item_version AFTER INSERT ON item_versions BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_text_insert AFTER INSERT ON text_derivatives BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
CREATE TRIGGER graph_revision_text_update AFTER UPDATE ON text_derivatives BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;
-- Source text completion decides proposal eligibility; other processors' job churn does not.
CREATE TRIGGER graph_revision_text_job AFTER UPDATE OF state ON processing_jobs
WHEN NEW.processor_key = 'source-text-v1' AND OLD.state IS NOT NEW.state
BEGIN UPDATE graph_revision SET revision = revision + 1 WHERE singleton = 1; END;

-- Duplicate detection looks entities up by their name without spacing, punctuation or case.
CREATE INDEX knowledge_entities_compact_name ON knowledge_entities(
    lower(replace(replace(replace(replace(replace(display_name, ' ', ''), '.', ''), '-', ''), '_', ''), '/', '')))
WHERE status = 'active';

-- Revisions widen from entity types to merges and relations.
CREATE TABLE knowledge_revisions_v33 (
    revision_id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_id TEXT NOT NULL REFERENCES knowledge_entities(entity_id) ON DELETE CASCADE,
    field TEXT NOT NULL CHECK (field IN ('entity_type', 'merge', 'relation')),
    relation_id TEXT REFERENCES knowledge_relations(relation_id) ON DELETE CASCADE,
    before_value TEXT CHECK (before_value IS NULL OR length(before_value) BETWEEN 1 AND 64),
    after_value TEXT NOT NULL CHECK (length(after_value) BETWEEN 1 AND 64),
    trigger TEXT NOT NULL CHECK (trigger IN ('rules', 'revocation', 'mentions', 'owner_lesson', 'relations', 'duplicates')),
    basis TEXT NOT NULL CHECK (length(basis) BETWEEN 1 AND 300),
    state TEXT NOT NULL CHECK (state IN ('applied', 'suggested', 'accepted', 'dismissed')),
    created_utc TEXT NOT NULL,
    decided_utc TEXT,
    CHECK ((state IN ('accepted', 'dismissed')) = (decided_utc IS NOT NULL)),
    CHECK ((field = 'relation') = (relation_id IS NOT NULL))
) STRICT;
INSERT INTO knowledge_revisions_v33
    (revision_id, entity_id, field, relation_id, before_value, after_value, trigger, basis, state, created_utc, decided_utc)
SELECT revision_id, entity_id, field, NULL, before_value, after_value, trigger, basis, state, created_utc, decided_utc
FROM knowledge_revisions;
DROP TABLE knowledge_revisions;
ALTER TABLE knowledge_revisions_v33 RENAME TO knowledge_revisions;
CREATE INDEX knowledge_revisions_entity ON knowledge_revisions(entity_id, revision_id);
CREATE INDEX knowledge_revisions_state ON knowledge_revisions(state, revision_id);
CREATE INDEX knowledge_revisions_relation ON knowledge_revisions(relation_id) WHERE relation_id IS NOT NULL;
CREATE UNIQUE INDEX knowledge_revisions_open_proposal ON knowledge_revisions(entity_id, field, after_value, COALESCE(relation_id, ''))
WHERE state IN ('suggested', 'dismissed');

PRAGMA user_version = 33;
