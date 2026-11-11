-- Typed relations between proposed entities and a flag for evidence that reads like an
-- instruction to the model. Rebuilt under the migration runner's deferred foreign-key check.
DROP VIEW eligible_enrichment_proposals;
DROP INDEX enrichment_proposals_item;

CREATE TABLE enrichment_proposals_v13 (
    proposal_id TEXT PRIMARY KEY,
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    processor_key TEXT NOT NULL,
    source_digest TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('topic', 'entity', 'relation')),
    entity_type TEXT CHECK (entity_type IS NULL OR entity_type IN
        ('person', 'organization', 'project', 'technology', 'concept', 'place',
         'document', 'event', 'other')),
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

INSERT INTO enrichment_proposals_v13
    (proposal_id, item_id, revision, processor_key, source_digest, kind, entity_type, value,
     evidence, evidence_offset, evidence_length, status, correction, created_utc, reviewed_utc)
SELECT proposal_id, item_id, revision, processor_key, source_digest, kind, entity_type, value,
       evidence, evidence_offset, evidence_length, status, correction, created_utc, reviewed_utc
FROM enrichment_proposals;

DROP TABLE enrichment_proposals;
ALTER TABLE enrichment_proposals_v13 RENAME TO enrichment_proposals;
CREATE INDEX enrichment_proposals_item ON enrichment_proposals(item_id, revision);
-- NULL relation ends would be distinct in a table UNIQUE constraint; coalesce them instead.
CREATE UNIQUE INDEX enrichment_proposals_identity ON enrichment_proposals(
    item_id, revision, processor_key, kind, evidence_offset, value,
    COALESCE(relation_subject, ''), COALESCE(relation_object, ''));

-- Current revision of an active source with verified text. A semantic-extract-v1 result is
-- superseded once semantic-extract-v2 has run for the same revision.
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

-- Canonical knowledge layer. Entities are owner-governed identities that proposals link to;
-- visibility of each link still follows the linked proposal's eligibility and review status.
CREATE TABLE knowledge_entities (
    entity_id TEXT PRIMARY KEY,
    display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 120),
    entity_type TEXT CHECK (entity_type IS NULL OR entity_type IN
        ('person', 'organization', 'project', 'technology', 'concept', 'place',
         'document', 'event', 'other')),
    type_origin TEXT NOT NULL CHECK (type_origin IN ('inferred', 'verified', 'owner')),
    status TEXT NOT NULL CHECK (status IN ('active', 'merged', 'rejected')),
    merged_into TEXT REFERENCES knowledge_entities(entity_id) ON DELETE RESTRICT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    CHECK ((status = 'merged') = (merged_into IS NOT NULL)),
    CHECK (merged_into IS NULL OR merged_into <> entity_id)
) STRICT;

CREATE TABLE entity_aliases (
    alias_key TEXT PRIMARY KEY CHECK (length(alias_key) BETWEEN 1 AND 120),
    entity_id TEXT NOT NULL REFERENCES knowledge_entities(entity_id) ON DELETE RESTRICT,
    display TEXT NOT NULL CHECK (length(display) BETWEEN 1 AND 120),
    origin TEXT NOT NULL CHECK (origin IN ('mention', 'owner')),
    created_utc TEXT NOT NULL
) STRICT;

CREATE INDEX entity_aliases_entity ON entity_aliases(entity_id);

CREATE TABLE entity_mentions (
    proposal_id TEXT PRIMARY KEY REFERENCES enrichment_proposals(proposal_id) ON DELETE RESTRICT,
    entity_id TEXT NOT NULL REFERENCES knowledge_entities(entity_id) ON DELETE RESTRICT,
    created_utc TEXT NOT NULL
) STRICT;

CREATE INDEX entity_mentions_entity ON entity_mentions(entity_id);

CREATE TABLE knowledge_relations (
    relation_id TEXT PRIMARY KEY,
    subject_id TEXT NOT NULL REFERENCES knowledge_entities(entity_id) ON DELETE RESTRICT,
    predicate TEXT NOT NULL CHECK (predicate IN
        ('uses', 'part_of', 'contains', 'depends_on', 'implements', 'parses', 'produces',
         'created_by', 'owned_by', 'works_on', 'member_of', 'located_in', 'pays',
         'references', 'related_to')),
    object_id TEXT NOT NULL REFERENCES knowledge_entities(entity_id) ON DELETE RESTRICT,
    origin TEXT NOT NULL CHECK (origin IN ('model', 'owner')),
    proposal_id TEXT REFERENCES enrichment_proposals(proposal_id) ON DELETE RESTRICT,
    created_utc TEXT NOT NULL,
    CHECK (subject_id <> object_id),
    CHECK ((origin = 'model') = (proposal_id IS NOT NULL))
) STRICT;

CREATE INDEX knowledge_relations_subject ON knowledge_relations(subject_id);
CREATE INDEX knowledge_relations_object ON knowledge_relations(object_id);

CREATE TABLE entity_decisions (
    decision_id TEXT PRIMARY KEY,
    entity_id TEXT NOT NULL REFERENCES knowledge_entities(entity_id) ON DELETE RESTRICT,
    action TEXT NOT NULL CHECK (action IN ('rename', 'set_type', 'merge', 'reject', 'restore', 'relate')),
    detail TEXT NOT NULL CHECK (length(detail) BETWEEN 1 AND 300),
    decided_utc TEXT NOT NULL
) STRICT;

CREATE INDEX entity_decisions_entity ON entity_decisions(entity_id, decided_utc);

-- One definition of which entity links count: the proposal is eligible, not rejected, and,
-- when its evidence reads like an instruction to the model, explicitly accepted or corrected.
CREATE VIEW visible_entity_mentions AS
SELECT m.entity_id, p.proposal_id, p.item_id, p.revision, p.source_id, p.entity_type,
       p.evidence, p.evidence_offset, p.status, p.flags
FROM entity_mentions m
JOIN eligible_enrichment_proposals p ON p.proposal_id = m.proposal_id
WHERE p.status <> 'rejected'
  AND (p.flags IS NULL OR p.status IN ('accepted', 'corrected'));

ALTER TABLE enrichment_runs ADD COLUMN segments_reused INTEGER
    CHECK (segments_reused IS NULL OR segments_reused >= 0);

-- Prompt v2 adds relations and owner-confirmed hints: stop unstarted v1 work and backfill v2
-- for current segmented revisions, newest first, behind new work.
UPDATE processing_jobs
SET state = 'canceled', lease_key = NULL, lease_until_utc = NULL, failure_code = NULL
WHERE processor_key = 'semantic-extract-v1' AND state IN ('queued', 'running', 'failed');

INSERT INTO processing_jobs
    (item_id, revision, processor_key, state, attempt, total_attempts,
     created_utc, updated_utc, priority)
SELECT j.item_id, j.revision, 'semantic-extract-v2', 'queued', 0, 0, j.updated_utc, j.updated_utc,
       -ROW_NUMBER() OVER (ORDER BY v.observed_utc DESC, j.item_id)
FROM processing_jobs j
JOIN item_versions v ON v.item_id = j.item_id AND v.revision = j.revision
JOIN items i ON i.item_id = j.item_id
JOIN sources s ON s.source_id = i.source_id
WHERE j.processor_key = 'text-segments-v1' AND j.state = 'completed'
  AND s.status = 'active'
  AND j.revision = (SELECT MAX(latest.revision) FROM item_versions latest
                    WHERE latest.item_id = j.item_id);

PRAGMA user_version = 13;
