DROP VIEW IF EXISTS technical_entity_types;
-- Expand the closed knowledge vocabulary while preserving proposal, entity, edge and review ids.
DROP VIEW confirmed_entity_mentions;
DROP VIEW visible_entity_mentions;
DROP VIEW proposal_verdicts;
DROP VIEW eligible_enrichment_proposals;

CREATE TABLE enrichment_proposals_v30 (
    proposal_id TEXT PRIMARY KEY,
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    processor_key TEXT NOT NULL,
    source_digest TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('topic', 'entity', 'relation')),
    entity_type TEXT CHECK (entity_type IS NULL OR entity_type IN
        ('person', 'organization', 'project', 'technology', 'concept', 'place',
         'document', 'event', 'other', 'class', 'function', 'binary_module', 'data_asset')),
    relation_subject TEXT CHECK (relation_subject IS NULL OR length(relation_subject) BETWEEN 1 AND 120),
    relation_object TEXT CHECK (relation_object IS NULL OR length(relation_object) BETWEEN 1 AND 120),
    value TEXT NOT NULL CHECK (length(value) BETWEEN 1 AND 120),
    evidence TEXT NOT NULL CHECK (length(evidence) BETWEEN 1 AND 240),
    evidence_offset INTEGER NOT NULL CHECK (evidence_offset >= 0),
    evidence_length INTEGER NOT NULL CHECK (evidence_length = length(evidence)),
    flags TEXT CHECK (flags IS NULL OR flags = 'instruction_like'),
    status TEXT NOT NULL CHECK (status IN ('pending', 'accepted', 'rejected', 'corrected')),
    correction TEXT CHECK (correction IS NULL OR length(correction) BETWEEN 1 AND 120),
    created_utc TEXT NOT NULL,
    reviewed_utc TEXT,
    FOREIGN KEY (item_id, revision, processor_key)
        REFERENCES enrichment_runs(item_id, revision, processor_key) ON DELETE RESTRICT,
    CHECK ((status = 'corrected') = (correction IS NOT NULL)),
    CHECK ((kind = 'entity') = (entity_type IS NOT NULL)),
    CHECK ((kind = 'relation') = (relation_subject IS NOT NULL AND relation_object IS NOT NULL))
) STRICT;
INSERT INTO enrichment_proposals_v30 SELECT * FROM enrichment_proposals;
DROP TABLE enrichment_proposals;
ALTER TABLE enrichment_proposals_v30 RENAME TO enrichment_proposals;

CREATE INDEX enrichment_proposals_item ON enrichment_proposals(item_id, revision);
CREATE UNIQUE INDEX enrichment_proposals_identity ON enrichment_proposals(
    item_id, revision, processor_key, kind, evidence_offset, value,
    COALESCE(relation_subject, ''), COALESCE(relation_object, ''));

CREATE TABLE knowledge_entities_v30 (
    entity_id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 120),
    entity_type TEXT CHECK (entity_type IS NULL OR entity_type IN
        ('person', 'organization', 'project', 'technology', 'concept', 'place',
         'document', 'event', 'other', 'class', 'function', 'binary_module', 'data_asset')),
    type_origin TEXT NOT NULL CHECK (type_origin IN ('inferred', 'verified', 'owner')),
    status TEXT NOT NULL CHECK (status IN ('active', 'merged', 'rejected')),
    merged_into TEXT REFERENCES knowledge_entities(entity_id) ON DELETE RESTRICT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    CHECK ((status = 'merged') = (merged_into IS NOT NULL)),
    CHECK (merged_into IS NULL OR merged_into <> entity_id)
) STRICT;
INSERT INTO knowledge_entities_v30 SELECT * FROM knowledge_entities;
DROP TABLE knowledge_entities;
ALTER TABLE knowledge_entities_v30 RENAME TO knowledge_entities;



CREATE TABLE knowledge_relations_v30 (
    relation_id TEXT PRIMARY KEY,
    subject_id TEXT NOT NULL REFERENCES knowledge_entities(entity_id) ON DELETE RESTRICT,
    predicate TEXT NOT NULL CHECK (predicate IN
        ('uses', 'part_of', 'contains', 'depends_on', 'implements', 'parses', 'produces',
         'created_by', 'owned_by', 'works_on', 'member_of', 'located_in', 'pays',
         'references', 'related_to', 'calls', 'inherits_from')),
    object_id TEXT NOT NULL REFERENCES knowledge_entities(entity_id) ON DELETE RESTRICT,
    origin TEXT NOT NULL CHECK (origin IN ('model', 'owner')),
    proposal_id TEXT REFERENCES enrichment_proposals(proposal_id) ON DELETE RESTRICT,
    created_utc TEXT NOT NULL,
    CHECK (subject_id <> object_id),
    CHECK ((origin = 'model') = (proposal_id IS NOT NULL))
) STRICT;
INSERT INTO knowledge_relations_v30 SELECT * FROM knowledge_relations;
DROP TABLE knowledge_relations;
ALTER TABLE knowledge_relations_v30 RENAME TO knowledge_relations;

CREATE INDEX knowledge_relations_subject ON knowledge_relations(subject_id);
CREATE INDEX knowledge_relations_object ON knowledge_relations(object_id);

CREATE TABLE verification_items_v30 (
    request_id TEXT NOT NULL REFERENCES verification_requests(request_id) ON DELETE RESTRICT,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 1),
    proposal_id TEXT REFERENCES enrichment_proposals(proposal_id) ON DELETE RESTRICT,
    entity_id TEXT REFERENCES knowledge_entities(entity_id) ON DELETE RESTRICT,
    included INTEGER NOT NULL CHECK (included IN (0, 1)),
    verdict TEXT CHECK (verdict IS NULL OR verdict IN ('supported', 'unsupported', 'uncertain')),
    corrected_type TEXT CHECK (corrected_type IS NULL OR corrected_type IN
        ('person', 'organization', 'project', 'technology', 'concept', 'place',
         'document', 'event', 'other', 'class', 'function', 'binary_module', 'data_asset')),
    note TEXT CHECK (note IS NULL OR length(note) <= 300),
    PRIMARY KEY (request_id, ordinal),
    CHECK ((proposal_id IS NULL) <> (entity_id IS NULL))
) STRICT;
INSERT INTO verification_items_v30 SELECT * FROM verification_items;
DROP TABLE verification_items;
ALTER TABLE verification_items_v30 RENAME TO verification_items;

CREATE INDEX verification_items_proposal ON verification_items(proposal_id);
CREATE INDEX verification_items_entity ON verification_items(entity_id);

CREATE VIEW eligible_enrichment_proposals AS
SELECT p.*, s.source_id
FROM enrichment_proposals p
JOIN item_versions v ON v.item_id = p.item_id AND v.revision = p.revision
    AND v.digest = p.source_digest
JOIN items i ON i.item_id = p.item_id
JOIN sources s ON s.source_id = i.source_id
JOIN text_derivatives d ON d.item_id = p.item_id
    AND d.revision = p.revision AND d.source_digest = p.source_digest
    AND d.processor_key = 'source-text-v1'
JOIN processing_jobs j ON j.item_id = d.item_id
    AND j.revision = d.revision AND j.processor_key = d.processor_key
WHERE s.status = 'active'
    AND j.state = 'completed' AND d.outcome = 'completed'
    AND p.revision = (SELECT MAX(current.revision) FROM item_versions current
                      WHERE current.item_id = p.item_id)
    AND NOT (p.processor_key = 'semantic-extract-v1' AND EXISTS (
        SELECT 1 FROM enrichment_runs successor
        WHERE successor.item_id = p.item_id AND successor.revision = p.revision
          AND successor.processor_key = 'semantic-extract-v2'));

CREATE VIEW proposal_verdicts AS
SELECT i.proposal_id, i.verdict
FROM verification_items i
JOIN verification_requests r ON r.request_id = i.request_id
WHERE r.status = 'completed' AND i.proposal_id IS NOT NULL AND i.verdict IS NOT NULL
  AND r.completed_utc = (
      SELECT MAX(r2.completed_utc) FROM verification_items i2
      JOIN verification_requests r2 ON r2.request_id = i2.request_id
      WHERE r2.status = 'completed' AND i2.proposal_id = i.proposal_id AND i2.verdict IS NOT NULL);

CREATE VIEW visible_entity_mentions AS
SELECT m.entity_id, p.proposal_id, p.item_id, p.revision, p.source_id, p.entity_type,
       p.evidence, p.evidence_offset, p.status, p.flags, v.verdict, p.processor_key
FROM entity_mentions m
JOIN eligible_enrichment_proposals p ON p.proposal_id = m.proposal_id
LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
WHERE p.status <> 'rejected'
  AND (p.status IN ('accepted', 'corrected')
       OR (COALESCE(v.verdict, '') <> 'unsupported'
           AND (p.flags IS NULL OR v.verdict = 'supported')));

CREATE VIEW confirmed_entity_mentions AS
SELECT m.*
FROM visible_entity_mentions m
JOIN eligible_enrichment_proposals p ON p.proposal_id = m.proposal_id
WHERE m.status IN ('accepted', 'corrected')
   OR m.verdict = 'supported'
   OR (p.processor_key = 'technical-evidence-v1' AND m.flags IS NULL)
   OR (p.processor_key = 'intake-understand-v1'
       AND EXISTS (SELECT 1 FROM intake_documents d
                   WHERE d.item_id = p.item_id AND d.revision = p.revision
                     AND d.tier IN ('strong', 'strongest', 'reused')));

CREATE VIEW technical_entity_types AS
SELECT m.entity_id, MIN(m.entity_type) AS entity_type
FROM visible_entity_mentions m
JOIN eligible_enrichment_proposals p ON p.proposal_id = m.proposal_id
WHERE p.processor_key = 'technical-evidence-v1' AND m.flags IS NULL
GROUP BY m.entity_id HAVING COUNT(DISTINCT m.entity_type) = 1;

-- Existing current text enters the new local-only processor once. Keep all existing reviews.
INSERT INTO processing_jobs
    (item_id, revision, processor_key, state, attempt, total_attempts, created_utc, updated_utc, priority)
SELECT j.item_id, j.revision, 'technical-evidence-v1', 'queued', 0, 0, j.updated_utc, j.updated_utc, 0
FROM processing_jobs j
JOIN text_derivatives t ON t.item_id = j.item_id AND t.revision = j.revision AND t.processor_key = j.processor_key
JOIN items i ON i.item_id = j.item_id
JOIN sources s ON s.source_id = i.source_id
WHERE j.processor_key = 'source-text-v1' AND j.state = 'completed' AND t.outcome = 'completed'
  AND s.status = 'active'
  AND j.revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = j.item_id)
ON CONFLICT(item_id, revision, processor_key) DO NOTHING;

-- An old hidden cap must not block local processing. Keep remote plan limits.
UPDATE verification_providers SET daily_limit = 0 WHERE provider_id = 'ollama';

PRAGMA user_version = 30;
