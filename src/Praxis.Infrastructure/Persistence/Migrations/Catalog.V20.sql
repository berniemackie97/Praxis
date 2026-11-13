-- Separate source processing classification from per-request, destination-specific
-- disclosure consent. Existing sources remain local-only; no approval is grandfathered.
CREATE TABLE source_verification_policy (
    source_id TEXT PRIMARY KEY REFERENCES sources(source_id) ON DELETE RESTRICT,
    classification TEXT NOT NULL CHECK (classification IN ('local_only', 'remote_verification')),
    revision INTEGER NOT NULL CHECK (revision >= 1),
    changed_utc TEXT NOT NULL
) STRICT;

CREATE TABLE verification_disclosure_grants (
    request_id TEXT PRIMARY KEY REFERENCES verification_requests(request_id) ON DELETE RESTRICT,
    provider_id TEXT NOT NULL REFERENCES verification_providers(provider_id) ON DELETE RESTRICT,
    provider_revision INTEGER NOT NULL CHECK (provider_revision >= 1),
    granted_utc TEXT NOT NULL,
    expires_utc TEXT NOT NULL,
    revoked_utc TEXT
) STRICT;

CREATE TABLE verification_disclosure_sources (
    request_id TEXT NOT NULL REFERENCES verification_disclosure_grants(request_id) ON DELETE RESTRICT,
    source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE RESTRICT,
    policy_revision INTEGER NOT NULL CHECK (policy_revision >= 1),
    PRIMARY KEY (request_id, source_id)
) STRICT;

CREATE TABLE verification_disclosure_audit (
    event_id TEXT PRIMARY KEY,
    request_id TEXT REFERENCES verification_requests(request_id) ON DELETE RESTRICT,
    source_id TEXT REFERENCES sources(source_id) ON DELETE RESTRICT,
    provider_id TEXT,
    operation TEXT NOT NULL CHECK (operation IN
        ('classification', 'approval', 'dispatch', 'completion', 'revocation', 'denial')),
    result TEXT NOT NULL CHECK (result IN ('allowed', 'denied')),
    reason_code TEXT NOT NULL CHECK (length(reason_code) BETWEEN 1 AND 64),
    occurred_utc TEXT NOT NULL
) STRICT;
CREATE INDEX verification_disclosure_audit_recent
    ON verification_disclosure_audit(occurred_utc DESC, event_id);

-- Requests approved before source classifications and scope snapshots existed cannot
-- prove permission for external disclosure. They must be reviewed again.
UPDATE verification_invocations
SET status = 'failed', finished_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    failure_code = 'disclosure_denied'
WHERE status = 'running' AND provider_id <> 'ollama';
UPDATE verification_requests
SET status = 'failed', completed_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
    failure_code = 'disclosure_denied',
    lease_key = NULL, lease_until_utc = NULL, retry_after_utc = NULL
WHERE status IN ('approved', 'running') AND provider_id <> 'ollama';

PRAGMA user_version = 20;
