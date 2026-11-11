-- Proposals generalize from literal topics to topics and typed entities, with an explicit
-- evidence span. Rebuilt with foreign keys deferred to the migration runner's final check.
DROP VIEW eligible_enrichment_proposals;
DROP INDEX enrichment_proposals_item;

CREATE TABLE enrichment_proposals_v12 (
    proposal_id TEXT PRIMARY KEY,
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    processor_key TEXT NOT NULL,
    source_digest TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('topic', 'entity')),
    entity_type TEXT CHECK (entity_type IS NULL OR entity_type IN
        ('person', 'organization', 'project', 'technology', 'concept', 'place',
         'document', 'event', 'other')),
    value TEXT NOT NULL CHECK (length(value) BETWEEN 1 AND 120),
    evidence TEXT NOT NULL CHECK (length(evidence) BETWEEN 1 AND 240),
    evidence_offset INTEGER NOT NULL CHECK (evidence_offset >= 0),
    evidence_length INTEGER NOT NULL CHECK (evidence_length = length(evidence)),
    status TEXT NOT NULL CHECK (status IN ('pending', 'accepted', 'rejected', 'corrected')),
    correction TEXT CHECK (correction IS NULL OR length(correction) BETWEEN 1 AND 120),
    created_utc TEXT NOT NULL,
    reviewed_utc TEXT,
    UNIQUE(item_id, revision, processor_key, kind, evidence_offset, value),
    FOREIGN KEY (item_id, revision, processor_key)
        REFERENCES enrichment_runs(item_id, revision, processor_key) ON DELETE RESTRICT,
    CHECK ((status = 'corrected') = (correction IS NOT NULL)),
    CHECK ((kind = 'entity') = (entity_type IS NOT NULL))
) STRICT;

INSERT INTO enrichment_proposals_v12
    (proposal_id, item_id, revision, processor_key, source_digest, kind, entity_type, value,
     evidence, evidence_offset, evidence_length, status, correction, created_utc, reviewed_utc)
SELECT proposal_id, item_id, revision, processor_key, source_digest, kind, NULL, value,
       evidence, evidence_offset, length(evidence), status, correction, created_utc, reviewed_utc
FROM enrichment_proposals;

DROP TABLE enrichment_proposals;
ALTER TABLE enrichment_proposals_v12 RENAME TO enrichment_proposals;
CREATE INDEX enrichment_proposals_item ON enrichment_proposals(item_id, revision);

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
                      WHERE current.item_id = p.item_id);

-- Model provenance and coverage for model-produced runs; NULL for deterministic producers.
ALTER TABLE enrichment_runs ADD COLUMN model_provider TEXT;
ALTER TABLE enrichment_runs ADD COLUMN model_name TEXT;
ALTER TABLE enrichment_runs ADD COLUMN model_digest TEXT;
ALTER TABLE enrichment_runs ADD COLUMN input_tokens INTEGER CHECK (input_tokens IS NULL OR input_tokens >= 0);
ALTER TABLE enrichment_runs ADD COLUMN output_tokens INTEGER CHECK (output_tokens IS NULL OR output_tokens >= 0);
ALTER TABLE enrichment_runs ADD COLUMN duration_ms INTEGER CHECK (duration_ms IS NULL OR duration_ms >= 0);
ALTER TABLE enrichment_runs ADD COLUMN segments_examined INTEGER CHECK (segments_examined IS NULL OR segments_examined >= 0);
ALTER TABLE enrichment_runs ADD COLUMN segments_total INTEGER CHECK (segments_total IS NULL OR segments_total >= 0);
ALTER TABLE enrichment_runs ADD COLUMN rejected_count INTEGER CHECK (rejected_count IS NULL OR rejected_count >= 0);

-- Existing segmented revisions get model enrichment queued newest first, behind new work.
INSERT INTO processing_jobs
    (item_id, revision, processor_key, state, attempt, total_attempts,
     created_utc, updated_utc, priority)
SELECT j.item_id, j.revision, 'semantic-extract-v1', 'queued', 0, 0, j.updated_utc, j.updated_utc,
       -ROW_NUMBER() OVER (ORDER BY v.observed_utc DESC, j.item_id)
FROM processing_jobs j
JOIN item_versions v ON v.item_id = j.item_id AND v.revision = j.revision
JOIN items i ON i.item_id = j.item_id
JOIN sources s ON s.source_id = i.source_id
WHERE j.processor_key = 'text-segments-v1' AND j.state = 'completed'
  AND s.status = 'active'
  AND j.revision = (SELECT MAX(latest.revision) FROM item_versions latest
                    WHERE latest.item_id = j.item_id);

PRAGMA user_version = 12;
