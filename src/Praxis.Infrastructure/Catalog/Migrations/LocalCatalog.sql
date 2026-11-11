PRAGMA user_version = 1;

CREATE TABLE original_objects (
    digest TEXT PRIMARY KEY
        CHECK (length(digest) = 64 AND digest NOT GLOB '*[^0-9a-f]*'),
    byte_length INTEGER NOT NULL CHECK (byte_length >= 0),
    recorded_utc TEXT NOT NULL
) STRICT;

CREATE TABLE sources (
    source_id TEXT PRIMARY KEY,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('file', 'url', 'voice', 'browser', 'note', 'connector', 'agent')),
    locator TEXT NOT NULL CHECK (length(locator) > 0),
    created_utc TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'revoked')),
    UNIQUE (source_kind, locator)
) STRICT;

CREATE TABLE items (
    item_id TEXT PRIMARY KEY,
    source_id TEXT NOT NULL UNIQUE REFERENCES sources(source_id) ON DELETE RESTRICT
) STRICT;

CREATE TABLE item_versions (
    item_id TEXT NOT NULL REFERENCES items(item_id) ON DELETE RESTRICT,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    digest TEXT NOT NULL REFERENCES original_objects(digest) ON DELETE RESTRICT,
    observed_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, revision)
) STRICT;

CREATE TABLE ingest_requests (
    request_key TEXT PRIMARY KEY,
    source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE RESTRICT,
    state TEXT NOT NULL CHECK (state IN ('queued', 'staging', 'committed', 'failed', 'canceled')),
    attempt INTEGER NOT NULL DEFAULT 0 CHECK (attempt >= 0),
    failure_code TEXT,
    CHECK ((state = 'failed') = (failure_code IS NOT NULL))
) STRICT;

CREATE INDEX item_versions_by_digest ON item_versions(digest);
CREATE INDEX ingest_requests_by_state ON ingest_requests(state);
