CREATE TABLE source_locator_moves (
    move_id TEXT PRIMARY KEY,
    source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE RESTRICT,
    old_locator TEXT NOT NULL,
    new_locator TEXT NOT NULL,
    digest TEXT NOT NULL REFERENCES original_objects(digest) ON DELETE RESTRICT,
    recorded_utc TEXT NOT NULL,
    CHECK (old_locator <> new_locator)
) STRICT;

CREATE INDEX source_locator_moves_source ON source_locator_moves(source_id, recorded_utc);

PRAGMA user_version = 8;
