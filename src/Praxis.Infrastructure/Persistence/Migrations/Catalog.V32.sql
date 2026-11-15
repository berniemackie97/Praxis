-- Reconsolidation (#463): Praxis re-examines entities whose evidence changed and records every
-- correction it makes or proposes as a revision. Owner and verified decisions are never revised.

-- Entities waiting for a second look, one row each. A later trigger never erases an earlier
-- owner lesson: the lesson keeps the type Praxis held before the owner corrected it.
CREATE TABLE reconsolidation_queue (
    entity_id TEXT PRIMARY KEY REFERENCES knowledge_entities(entity_id) ON DELETE CASCADE,
    reason TEXT NOT NULL CHECK (reason IN ('rules', 'revocation', 'mentions', 'owner_lesson')),
    previous_type TEXT CHECK (previous_type IS NULL OR length(previous_type) BETWEEN 1 AND 32),
    enqueued_utc TEXT NOT NULL
) STRICT;

CREATE INDEX reconsolidation_queue_order ON reconsolidation_queue(enqueued_utc, entity_id);

-- Every change Praxis applied to its own judgment, and every change it proposes to the owner.
CREATE TABLE knowledge_revisions (
    revision_id INTEGER PRIMARY KEY AUTOINCREMENT,
    entity_id TEXT NOT NULL REFERENCES knowledge_entities(entity_id) ON DELETE CASCADE,
    field TEXT NOT NULL CHECK (field = 'entity_type'),
    before_value TEXT CHECK (before_value IS NULL OR length(before_value) BETWEEN 1 AND 32),
    after_value TEXT NOT NULL CHECK (length(after_value) BETWEEN 1 AND 32),
    trigger TEXT NOT NULL CHECK (trigger IN ('rules', 'revocation', 'mentions', 'owner_lesson', 'relations')),
    basis TEXT NOT NULL CHECK (length(basis) BETWEEN 1 AND 300),
    state TEXT NOT NULL CHECK (state IN ('applied', 'suggested', 'accepted', 'dismissed')),
    created_utc TEXT NOT NULL,
    decided_utc TEXT,
    CHECK ((state IN ('accepted', 'dismissed')) = (decided_utc IS NOT NULL))
) STRICT;

CREATE INDEX knowledge_revisions_entity ON knowledge_revisions(entity_id, revision_id);
CREATE INDEX knowledge_revisions_state ON knowledge_revisions(state, revision_id);
-- One open or dismissed proposal per entity and outcome, so a dismissed idea is not proposed again.
CREATE UNIQUE INDEX knowledge_revisions_open_proposal ON knowledge_revisions(entity_id, field, after_value)
WHERE state IN ('suggested', 'dismissed');

-- A revoked source changes what its entities are known from. Queued processing for it can never
-- run (claims require an active source), so it is canceled rather than counted as waiting.
CREATE TRIGGER reconsolidation_source_revoked AFTER UPDATE OF status ON sources
WHEN NEW.status <> 'active'
BEGIN
    INSERT INTO reconsolidation_queue (entity_id, reason, previous_type, enqueued_utc)
    SELECT DISTINCT m.entity_id, 'revocation', NULL, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    FROM entity_mentions m
    JOIN enrichment_proposals p ON p.proposal_id = m.proposal_id
    JOIN items i ON i.item_id = p.item_id
    WHERE i.source_id = NEW.source_id
    ON CONFLICT(entity_id) DO NOTHING;

    UPDATE processing_jobs SET state = 'canceled', lease_key = NULL, lease_until_utc = NULL, updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
    WHERE state = 'queued' AND item_id IN (SELECT item_id FROM items WHERE source_id = NEW.source_id);
END;

-- An owner type correction is a lesson: remember what Praxis believed so look-alikes can be found.
CREATE TRIGGER reconsolidation_owner_type AFTER UPDATE OF entity_type, type_origin ON knowledge_entities
WHEN NEW.type_origin = 'owner' AND NEW.entity_type IS NOT NULL AND OLD.entity_type IS NOT NEW.entity_type
BEGIN
    INSERT INTO reconsolidation_queue (entity_id, reason, previous_type, enqueued_utc)
    VALUES (NEW.entity_id, 'owner_lesson', OLD.entity_type, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
    ON CONFLICT(entity_id) DO UPDATE SET reason = 'owner_lesson', previous_type = excluded.previous_type,
        enqueued_utc = excluded.enqueued_utc;
END;

-- A new relation, from a model or the owner, is new evidence about both of its entities.
CREATE TRIGGER reconsolidation_relation_added AFTER INSERT ON knowledge_relations
BEGIN
    INSERT INTO reconsolidation_queue (entity_id, reason, previous_type, enqueued_utc)
    VALUES (NEW.subject_id, 'mentions', NULL, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
           (NEW.object_id, 'mentions', NULL, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
    ON CONFLICT(entity_id) DO NOTHING;
END;

-- Jobs already orphaned by earlier revocations stop counting as queued work.
UPDATE processing_jobs SET state = 'canceled', lease_key = NULL, lease_until_utc = NULL, updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
WHERE state = 'queued' AND item_id IN (
    SELECT i.item_id FROM items i JOIN sources s ON s.source_id = i.source_id WHERE s.status <> 'active');

PRAGMA user_version = 32;
