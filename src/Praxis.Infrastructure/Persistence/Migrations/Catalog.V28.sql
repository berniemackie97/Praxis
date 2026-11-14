-- Machine-aware tuning (ADR 0050): small owner settings such as when the local tier drafts.
CREATE TABLE IF NOT EXISTS runtime_settings (
    key TEXT PRIMARY KEY CHECK (length(key) BETWEEN 1 AND 64),
    value TEXT NOT NULL CHECK (length(value) <= 256),
    updated_utc TEXT NOT NULL
) STRICT, WITHOUT ROWID;

PRAGMA user_version = 28;
