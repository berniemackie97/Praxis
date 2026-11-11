CREATE TABLE processing_jobs (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    processor_key TEXT NOT NULL CHECK (length(processor_key) BETWEEN 1 AND 64),
    state TEXT NOT NULL CHECK (state IN ('queued', 'running', 'completed', 'unsupported', 'failed', 'canceled')),
    attempt INTEGER NOT NULL DEFAULT 0 CHECK (attempt >= 0),
    total_attempts INTEGER NOT NULL DEFAULT 0 CHECK (total_attempts >= attempt),
    lease_key TEXT,
    lease_until_utc TEXT,
    failure_code TEXT,
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, revision, processor_key),
    FOREIGN KEY (item_id, revision) REFERENCES item_versions(item_id, revision) ON DELETE RESTRICT,
    CHECK ((state = 'running') = (lease_key IS NOT NULL AND lease_until_utc IS NOT NULL)),
    CHECK ((state = 'failed') = (failure_code IS NOT NULL))
) STRICT;

CREATE INDEX processing_jobs_ready ON processing_jobs(state, lease_until_utc, created_utc);

INSERT INTO processing_jobs
    (item_id, revision, processor_key, state, attempt, total_attempts,
     created_utc, updated_utc)
SELECT v.item_id, v.revision, 'source-text-v1',
       CASE WHEN s.status = 'revoked' THEN 'canceled' ELSE 'queued' END,
       0, 0, v.observed_utc, v.observed_utc
FROM item_versions v
JOIN items i ON i.item_id = v.item_id
JOIN sources s ON s.source_id = i.source_id;

PRAGMA user_version = 3;
