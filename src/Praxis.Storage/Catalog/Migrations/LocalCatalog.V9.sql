-- Topic proposals move from ad-hoc polling to leased processing jobs that are scheduled
-- when their prerequisite text extraction completes.
CREATE INDEX processing_jobs_processor_ready
    ON processing_jobs(processor_key, state, created_utc);

-- Revisions already examined by the heading producer keep their recorded outcome.
INSERT INTO processing_jobs
    (item_id, revision, processor_key, state, attempt, total_attempts, created_utc, updated_utc)
SELECT r.item_id, r.revision, 'markdown-heading-v1', 'completed', 0, 0, r.created_utc, r.created_utc
FROM enrichment_runs r
WHERE r.processor_key = 'markdown-heading-v1';

-- Every other completed text extraction gets a heading job: queued for the current revision
-- of an active source, canceled when the revision is superseded or the source is revoked.
INSERT INTO processing_jobs
    (item_id, revision, processor_key, state, attempt, total_attempts, created_utc, updated_utc)
SELECT j.item_id, j.revision, 'markdown-heading-v1',
       CASE WHEN s.status = 'active' AND j.revision = (
                SELECT MAX(latest.revision) FROM item_versions latest
                WHERE latest.item_id = j.item_id)
            THEN 'queued' ELSE 'canceled' END,
       0, 0, j.updated_utc, j.updated_utc
FROM processing_jobs j
JOIN items i ON i.item_id = j.item_id
JOIN sources s ON s.source_id = i.source_id
WHERE j.processor_key = 'source-text-v1' AND j.state = 'completed'
  AND NOT EXISTS (
      SELECT 1 FROM processing_jobs existing
      WHERE existing.item_id = j.item_id AND existing.revision = j.revision
        AND existing.processor_key = 'markdown-heading-v1');

PRAGMA user_version = 9;
