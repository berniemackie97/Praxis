-- Hand-off cascade (ADR 0049): how each tier contributed to a document's understanding, as one
-- owner-facing line ("local drafted 12 → strong kept 9, removed 3, added 4 → 13 verified").
CREATE TABLE IF NOT EXISTS intake_cascades (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    cascade TEXT NOT NULL CHECK (length(cascade) BETWEEN 1 AND 400),
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, revision),
    FOREIGN KEY (item_id, revision) REFERENCES item_versions(item_id, revision) ON DELETE CASCADE
) STRICT, WITHOUT ROWID;

PRAGMA user_version = 27;
