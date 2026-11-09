CREATE TABLE enrichment_runs (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    processor_key TEXT NOT NULL,
    source_digest TEXT NOT NULL REFERENCES original_objects(digest) ON DELETE RESTRICT,
    outcome TEXT NOT NULL CHECK (outcome IN ('generated', 'abstained')),
    created_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, revision, processor_key),
    FOREIGN KEY (item_id, revision)
        REFERENCES item_versions(item_id, revision) ON DELETE RESTRICT
) STRICT;

CREATE TABLE enrichment_proposals (
    proposal_id TEXT PRIMARY KEY,
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    processor_key TEXT NOT NULL,
    source_digest TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind = 'topic'),
    value TEXT NOT NULL CHECK (length(value) BETWEEN 1 AND 80),
    evidence TEXT NOT NULL CHECK (length(evidence) BETWEEN 1 AND 160),
    evidence_offset INTEGER NOT NULL CHECK (evidence_offset >= 0),
    status TEXT NOT NULL CHECK (status IN ('pending', 'accepted', 'rejected', 'corrected')),
    correction TEXT CHECK (correction IS NULL OR length(correction) BETWEEN 1 AND 80),
    created_utc TEXT NOT NULL,
    reviewed_utc TEXT,
    UNIQUE(item_id, revision, processor_key, evidence_offset, value),
    FOREIGN KEY (item_id, revision, processor_key)
        REFERENCES enrichment_runs(item_id, revision, processor_key) ON DELETE RESTRICT,
    CHECK ((status = 'corrected') = (correction IS NOT NULL))
) STRICT;

CREATE INDEX enrichment_proposals_item
    ON enrichment_proposals(item_id, revision);

CREATE TABLE enrichment_reviews (
    review_id TEXT PRIMARY KEY,
    proposal_id TEXT NOT NULL REFERENCES enrichment_proposals(proposal_id) ON DELETE RESTRICT,
    decision TEXT NOT NULL CHECK (decision IN ('accepted', 'rejected', 'corrected')),
    correction TEXT,
    reviewed_utc TEXT NOT NULL,
    CHECK ((decision = 'corrected') = (correction IS NOT NULL))
) STRICT;

CREATE INDEX enrichment_reviews_proposal ON enrichment_reviews(proposal_id, reviewed_utc);

PRAGMA user_version = 7;
