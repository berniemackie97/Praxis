-- Claim order: new work (priority 0) before backfilled work, which carries a negative
-- recency rank so the most recently observed revisions are processed first.
ALTER TABLE processing_jobs ADD COLUMN priority INTEGER NOT NULL DEFAULT 0 CHECK (priority <= 0);

DROP INDEX processing_jobs_processor_ready;
CREATE INDEX processing_jobs_processor_ready
    ON processing_jobs(processor_key, state, priority DESC, created_utc);

-- Deterministic structure-aware segments of completed text. Offsets and lengths are UTF-16
-- code units into the revision's text derivative; segments are contiguous and cover it exactly.
CREATE TABLE text_segments (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    processor_key TEXT NOT NULL CHECK (processor_key = 'text-segments-v1'),
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    start_offset INTEGER NOT NULL CHECK (start_offset >= 0),
    length INTEGER NOT NULL CHECK (length >= 1),
    heading_path TEXT CHECK (heading_path IS NULL OR length(heading_path) BETWEEN 1 AND 512),
    PRIMARY KEY (item_id, revision, ordinal),
    FOREIGN KEY (item_id, revision, processor_key)
        REFERENCES processing_jobs(item_id, revision, processor_key) ON DELETE RESTRICT
) STRICT;

INSERT INTO processing_jobs
    (item_id, revision, processor_key, state, attempt, total_attempts,
     created_utc, updated_utc, priority)
SELECT j.item_id, j.revision, 'text-segments-v1', 'queued', 0, 0, j.updated_utc, j.updated_utc,
       -ROW_NUMBER() OVER (ORDER BY v.observed_utc DESC, j.item_id)
FROM processing_jobs j
JOIN item_versions v ON v.item_id = j.item_id AND v.revision = j.revision
JOIN items i ON i.item_id = j.item_id
JOIN sources s ON s.source_id = i.source_id
WHERE j.processor_key = 'source-text-v1' AND j.state = 'completed'
  AND s.status = 'active'
  AND j.revision = (SELECT MAX(latest.revision) FROM item_versions latest
                    WHERE latest.item_id = j.item_id);

PRAGMA user_version = 10;
