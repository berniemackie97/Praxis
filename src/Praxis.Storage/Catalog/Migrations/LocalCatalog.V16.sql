-- Opt-in owner-local embedding configuration. Existing enrichment profiles are preserved.
-- No remote endpoint or implicit model is configured by this migration.
CREATE TABLE model_profiles_v16 (
    role TEXT PRIMARY KEY CHECK (role IN ('local-enrichment', 'local-embedding')),
    provider TEXT NOT NULL CHECK (provider IN ('ollama')),
    endpoint TEXT NOT NULL CHECK (length(endpoint) BETWEEN 1 AND 256),
    model TEXT NOT NULL CHECK (length(model) BETWEEN 1 AND 200),
    context_tokens INTEGER NOT NULL CHECK (context_tokens BETWEEN 2048 AND 262144),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    updated_utc TEXT NOT NULL
) STRICT;
INSERT INTO model_profiles_v16
SELECT role, provider, endpoint, model, context_tokens, enabled, updated_utc
FROM model_profiles;
DROP TABLE model_profiles;
ALTER TABLE model_profiles_v16 RENAME TO model_profiles;

-- Rebuildable, model-versioned embeddings of canonical text segments.
-- Query-time visibility is always checked against active latest revisions and completed text.
CREATE TABLE semantic_vectors (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    model_digest TEXT NOT NULL CHECK (length(model_digest) BETWEEN 1 AND 256),
    source_digest TEXT NOT NULL CHECK (length(source_digest) = 64),
    dimensions INTEGER NOT NULL CHECK (dimensions BETWEEN 1 AND 4096),
    vector BLOB NOT NULL CHECK (length(vector) = dimensions * 4),
    segment_text TEXT NOT NULL CHECK (length(segment_text) BETWEEN 1 AND 2000),
    recorded_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, revision, ordinal, model_digest),
    FOREIGN KEY (item_id, revision, ordinal)
        REFERENCES text_segments(item_id, revision, ordinal) ON DELETE RESTRICT
) STRICT;

CREATE INDEX semantic_vectors_model ON semantic_vectors(model_digest, item_id, revision);

CREATE VIEW eligible_semantic_segments AS
SELECT g.item_id, g.revision, g.ordinal, v.digest AS source_digest,
       e.source_id, e.locator, e.content, g.start_offset, g.length
FROM text_segments g
JOIN processing_jobs j ON j.item_id = g.item_id AND j.revision = g.revision
    AND j.processor_key = g.processor_key AND j.state = 'completed'
JOIN eligible_text_search e ON e.item_id = g.item_id AND e.revision = g.revision
JOIN item_versions v ON v.item_id = g.item_id AND v.revision = g.revision;

CREATE TRIGGER semantic_vectors_new_version AFTER INSERT ON item_versions
BEGIN
    DELETE FROM semantic_vectors WHERE item_id = NEW.item_id;
END;

CREATE TRIGGER semantic_vectors_source_revoked AFTER UPDATE OF status ON sources
WHEN NEW.status <> 'active'
BEGIN
    DELETE FROM semantic_vectors WHERE item_id IN (
        SELECT item_id FROM items WHERE source_id = NEW.source_id);
END;

PRAGMA user_version = 16;
