-- This is invocation metadata recorded by trusted local capture paths, not a
-- transferable permission or proof of an authenticated owner's identity. Existing receipts
-- predate this feature and deliberately have no such row.
CREATE TABLE capture_request_context (
    request_key TEXT PRIMARY KEY REFERENCES ingest_requests(request_key) ON DELETE RESTRICT,
    route TEXT NOT NULL CHECK (route IN
        ('direct_local', 'selected_file', 'selected_folder', 'watched_folder', 'quick_note')),
    authority_basis TEXT NOT NULL CHECK (authority_basis IN
        ('unattributed_local_call', 'owner_selected', 'owner_configured', 'owner_submitted')),
    scope TEXT CHECK (scope IS NULL OR length(scope) BETWEEN 1 AND 4096),
    recorded_utc TEXT NOT NULL,
    CHECK (
        (route = 'direct_local' AND authority_basis = 'unattributed_local_call' AND scope IS NULL)
        OR (route IN ('selected_file', 'selected_folder')
            AND authority_basis = 'owner_selected' AND scope IS NOT NULL)
        OR (route = 'watched_folder'
            AND authority_basis = 'owner_configured' AND scope IS NOT NULL)
        OR (route = 'quick_note'
            AND authority_basis = 'owner_submitted' AND scope IS NOT NULL)
    )
) STRICT;

CREATE INDEX ingest_requests_source_revision
    ON ingest_requests(source_id, revision, state);

PRAGMA user_version = 15;
