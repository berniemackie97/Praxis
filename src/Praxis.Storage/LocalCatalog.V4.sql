CREATE TABLE text_derivatives (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    processor_key TEXT NOT NULL,
    source_digest TEXT NOT NULL REFERENCES original_objects(digest) ON DELETE RESTRICT,
    outcome TEXT NOT NULL CHECK (outcome IN ('completed', 'unsupported')),
    content TEXT,
    reason_code TEXT,
    created_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, revision, processor_key),
    FOREIGN KEY (item_id, revision, processor_key)
        REFERENCES processing_jobs(item_id, revision, processor_key) ON DELETE RESTRICT,
    CHECK ((outcome = 'completed' AND content IS NOT NULL AND reason_code IS NULL)
        OR (outcome = 'unsupported' AND content IS NULL AND reason_code IS NOT NULL))
) STRICT;

CREATE INDEX text_derivatives_by_source ON text_derivatives(source_digest);

PRAGMA user_version = 4;
