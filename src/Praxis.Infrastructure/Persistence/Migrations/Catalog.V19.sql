-- A reviewed entity type may draw on several independent source revisions. Retain each
-- actual supporting proposal internally so an approval can be fenced when any excerpt
-- becomes ineligible, even if other entity mentions remain visible.
CREATE TABLE verification_contexts (
    request_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 1),
    proposal_id TEXT NOT NULL REFERENCES enrichment_proposals(proposal_id) ON DELETE RESTRICT,
    source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE RESTRICT,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    source_digest TEXT NOT NULL CHECK (length(source_digest) = 64),
    PRIMARY KEY (request_id, ordinal, proposal_id),
    FOREIGN KEY (request_id, ordinal)
        REFERENCES verification_items(request_id, ordinal) ON DELETE RESTRICT
) STRICT;

-- Older entity reviews cannot be backfilled safely: the original selection was not
-- persisted and their stored payload may already be redacted. Require a fresh review.
UPDATE verification_invocations
SET status = 'failed', finished_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    failure_code = 'source_changed'
WHERE status = 'running' AND request_id IN (
    SELECT DISTINCT i.request_id FROM verification_items i WHERE i.entity_id IS NOT NULL
);
UPDATE verification_requests
SET status = 'failed', completed_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    failure_code = 'source_changed',
    lease_key = NULL, lease_until_utc = NULL, retry_after_utc = NULL
WHERE status IN ('awaiting_approval', 'approved', 'running')
    AND EXISTS (
        SELECT 1 FROM verification_items i
        WHERE i.request_id = verification_requests.request_id AND i.entity_id IS NOT NULL
    );

PRAGMA user_version = 19;
