CREATE TABLE source_collections (
    collection_id TEXT PRIMARY KEY,
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 80),
    name_key TEXT NOT NULL UNIQUE CHECK (length(name_key) BETWEEN 1 AND 80),
    created_utc TEXT NOT NULL,
    archived_utc TEXT
) STRICT;

CREATE TABLE source_collection_memberships (
    collection_id TEXT NOT NULL
        REFERENCES source_collections(collection_id) ON DELETE RESTRICT,
    source_id TEXT NOT NULL
        REFERENCES sources(source_id) ON DELETE RESTRICT,
    created_utc TEXT NOT NULL,
    PRIMARY KEY (collection_id, source_id)
) STRICT;

CREATE INDEX source_collection_memberships_source
    ON source_collection_memberships(source_id);

PRAGMA user_version = 6;
