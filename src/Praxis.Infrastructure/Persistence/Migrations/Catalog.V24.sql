-- ADR 0045. Media moments, saved links and code projects.

-- Where each span of a transcript is spoken. Transcripts are stored as readable text; their
-- timing lives here so any character maps back to a moment in the media.
CREATE TABLE text_timings (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    processor_key TEXT NOT NULL CHECK (processor_key = 'source-text-v1'),
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    char_offset INTEGER NOT NULL CHECK (char_offset >= 0),
    char_length INTEGER NOT NULL CHECK (char_length >= 1),
    start_ms INTEGER NOT NULL CHECK (start_ms >= 0),
    end_ms INTEGER NOT NULL CHECK (end_ms >= start_ms),
    PRIMARY KEY (item_id, revision, processor_key, ordinal),
    FOREIGN KEY (item_id, revision, processor_key) REFERENCES processing_jobs(item_id, revision, processor_key) ON DELETE CASCADE
) STRICT, WITHOUT ROWID;

-- What each saved link's fetch produced: the captured snapshot, or why nothing was fetched.
CREATE TABLE link_snapshots (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    processor_key TEXT NOT NULL CHECK (processor_key = 'link-fetch-v1'),
    outcome TEXT NOT NULL CHECK (outcome IN ('completed', 'unsupported')),
    final_url TEXT CHECK (final_url IS NULL OR length(final_url) BETWEEN 8 AND 2048),
    http_status INTEGER NOT NULL DEFAULT 0 CHECK (http_status BETWEEN 0 AND 599),
    content_type TEXT CHECK (content_type IS NULL OR length(content_type) <= 128),
    snapshot_locator TEXT CHECK (snapshot_locator IS NULL OR length(snapshot_locator) <= 4096),
    reason TEXT CHECK (reason IS NULL OR length(reason) BETWEEN 1 AND 64),
    fetched_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, revision, processor_key),
    FOREIGN KEY (item_id, revision, processor_key) REFERENCES processing_jobs(item_id, revision, processor_key) ON DELETE CASCADE,
    CHECK ((outcome = 'completed') = (snapshot_locator IS NOT NULL))
) STRICT, WITHOUT ROWID;

-- What Praxis may fetch for saved links (ADR 0045). One row; absent means the default.
CREATE TABLE link_settings (
    settings_id INTEGER PRIMARY KEY CHECK (settings_id = 1),
    mode TEXT NOT NULL CHECK (mode IN ('off', 'saved_links')),
    blocked_hosts TEXT NOT NULL CHECK (json_valid(blocked_hosts) AND json_type(blocked_hosts) = 'array'),
    updated_utc TEXT NOT NULL
) STRICT;

-- Code projects understood as a whole (one row per detected project root).
CREATE TABLE intake_projects (
    project_id TEXT PRIMARY KEY CHECK (length(project_id) = 32),
    root TEXT NOT NULL CHECK (length(root) BETWEEN 1 AND 4096),
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 256),
    fingerprint TEXT NOT NULL CHECK (length(fingerprint) = 64),
    status TEXT NOT NULL CHECK (status IN ('understood', 'partially_verified', 'map_only')),
    reason TEXT CHECK (reason IS NULL OR length(reason) BETWEEN 1 AND 64),
    details TEXT NOT NULL CHECK (json_valid(details) AND length(details) <= 262144),
    members INTEGER NOT NULL CHECK (members >= 0),
    tier TEXT CHECK (tier IS NULL OR tier IN ('local', 'strong', 'strongest')),
    provider_id TEXT CHECK (provider_id IS NULL OR length(provider_id) BETWEEN 1 AND 64),
    input_tokens INTEGER NOT NULL DEFAULT 0 CHECK (input_tokens >= 0),
    output_tokens INTEGER NOT NULL DEFAULT 0 CHECK (output_tokens >= 0),
    updated_utc TEXT NOT NULL
) STRICT;

-- Saved link files captured before this version are fetched once (subject to link settings).
INSERT INTO processing_jobs (item_id, revision, processor_key, state, attempt, total_attempts, created_utc, updated_utc)
SELECT j.item_id, j.revision, 'link-fetch-v1', 'queued', 0, 0, j.updated_utc, j.updated_utc
FROM processing_jobs j
JOIN items i ON i.item_id = j.item_id
JOIN sources s ON s.source_id = i.source_id
WHERE j.processor_key = 'source-text-v1' AND j.state = 'completed' AND s.status = 'active'
  AND (lower(s.locator) LIKE '%.webloc' OR lower(s.locator) LIKE '%.url')
  AND j.revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = j.item_id)
ON CONFLICT(item_id, revision, processor_key) DO NOTHING;

PRAGMA user_version = 24;
