CREATE TABLE search_index_keys (
    rowid INTEGER PRIMARY KEY,
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE RESTRICT,
    UNIQUE (item_id, revision),
    FOREIGN KEY (item_id, revision)
        REFERENCES item_versions(item_id, revision) ON DELETE RESTRICT
) STRICT;

CREATE INDEX search_index_keys_source ON search_index_keys(source_id);

CREATE VIRTUAL TABLE text_search USING fts5(
    locator,
    content,
    tokenize = 'unicode61'
);

CREATE VIEW eligible_text_search AS
SELECT d.item_id, d.revision, s.source_id, s.locator, d.content
FROM text_derivatives d
JOIN processing_jobs j
    ON j.item_id = d.item_id AND j.revision = d.revision
    AND j.processor_key = d.processor_key
JOIN item_versions v
    ON v.item_id = d.item_id AND v.revision = d.revision
    AND v.digest = d.source_digest
JOIN items i ON i.item_id = d.item_id
JOIN sources s ON s.source_id = i.source_id
WHERE d.processor_key = 'source-text-v1'
    AND d.outcome = 'completed' AND j.state = 'completed'
    AND s.status = 'active'
    AND d.revision = (
        SELECT MAX(latest.revision) FROM item_versions latest
        WHERE latest.item_id = d.item_id
    );

CREATE TRIGGER text_search_new_version AFTER INSERT ON item_versions
BEGIN
    DELETE FROM text_search WHERE rowid IN (
        SELECT rowid FROM search_index_keys WHERE item_id = NEW.item_id
    );
    DELETE FROM search_index_keys WHERE item_id = NEW.item_id;
END;

CREATE TRIGGER text_search_source_revoked AFTER UPDATE OF status ON sources
WHEN NEW.status != 'active'
BEGIN
    DELETE FROM text_search WHERE rowid IN (
        SELECT rowid FROM search_index_keys WHERE source_id = NEW.source_id
    );
    DELETE FROM search_index_keys WHERE source_id = NEW.source_id;
END;

CREATE TRIGGER text_search_job_completed AFTER UPDATE OF state ON processing_jobs
WHEN NEW.state = 'completed' AND NEW.processor_key = 'source-text-v1'
BEGIN
    INSERT INTO search_index_keys (item_id, revision, source_id)
    SELECT item_id, revision, source_id
    FROM eligible_text_search
    WHERE item_id = NEW.item_id AND revision = NEW.revision;

    INSERT INTO text_search (rowid, locator, content)
    SELECT k.rowid, e.locator, e.content
    FROM eligible_text_search e
    JOIN search_index_keys k ON k.item_id = e.item_id AND k.revision = e.revision
    WHERE e.item_id = NEW.item_id AND e.revision = NEW.revision;
END;

INSERT INTO search_index_keys (item_id, revision, source_id)
SELECT item_id, revision, source_id
FROM eligible_text_search ORDER BY item_id, revision;

INSERT INTO text_search (rowid, locator, content)
SELECT k.rowid, e.locator, e.content
FROM eligible_text_search e
JOIN search_index_keys k ON k.item_id = e.item_id AND k.revision = e.revision
ORDER BY e.item_id, e.revision;

PRAGMA user_version = 5;
