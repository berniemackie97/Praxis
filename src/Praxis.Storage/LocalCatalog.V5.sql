CREATE VIRTUAL TABLE text_search USING fts5(
    item_id UNINDEXED,
    revision UNINDEXED,
    source_id UNINDEXED,
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
    DELETE FROM text_search WHERE item_id = NEW.item_id;
END;

CREATE TRIGGER text_search_source_revoked AFTER UPDATE OF status ON sources
WHEN NEW.status != 'active'
BEGIN
    DELETE FROM text_search WHERE source_id = NEW.source_id;
END;

CREATE TRIGGER text_search_job_completed AFTER UPDATE OF state ON processing_jobs
WHEN NEW.state = 'completed' AND NEW.processor_key = 'source-text-v1'
BEGIN
    INSERT INTO text_search (item_id, revision, source_id, locator, content)
    SELECT item_id, revision, source_id, locator, content
    FROM eligible_text_search
    WHERE item_id = NEW.item_id AND revision = NEW.revision;
END;

INSERT INTO text_search (item_id, revision, source_id, locator, content)
SELECT item_id, revision, source_id, locator, content
FROM eligible_text_search ORDER BY item_id, revision;

PRAGMA user_version = 5;
