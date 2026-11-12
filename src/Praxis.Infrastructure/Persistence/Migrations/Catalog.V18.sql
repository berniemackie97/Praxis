-- Verification provider targets and approval revisions are independent of the model payload.
-- Existing approved CLI requests retain their exact provider configuration at revision 1.
CREATE TABLE verification_providers_v18 (
    provider_id TEXT PRIMARY KEY CHECK (provider_id IN ('ollama', 'codex', 'claude-code')),
    executable TEXT CHECK (executable IS NULL OR length(executable) BETWEEN 1 AND 1024),
    endpoint TEXT CHECK (endpoint IS NULL OR length(endpoint) BETWEEN 1 AND 256),
    model TEXT CHECK (model IS NULL OR length(model) BETWEEN 1 AND 100),
    cost_weight INTEGER NOT NULL CHECK (cost_weight BETWEEN 0 AND 1000),
    daily_limit INTEGER NOT NULL CHECK (daily_limit BETWEEN 1 AND 500),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    updated_utc TEXT NOT NULL,
    config_revision INTEGER NOT NULL DEFAULT 1 CHECK (config_revision >= 1),
    CHECK (
        (provider_id = 'ollama' AND executable IS NULL AND endpoint IS NOT NULL AND model IS NOT NULL)
        OR (provider_id IN ('codex', 'claude-code') AND executable IS NOT NULL AND endpoint IS NULL)
    )
) STRICT;

INSERT INTO verification_providers_v18
    (provider_id, executable, endpoint, model, cost_weight, daily_limit, enabled, updated_utc, config_revision)
SELECT provider_id, executable, NULL, model, cost_weight, daily_limit, enabled, updated_utc, 1
FROM verification_providers;

DROP TABLE verification_providers;
ALTER TABLE verification_providers_v18 RENAME TO verification_providers;

ALTER TABLE verification_requests
    ADD COLUMN approved_provider_revision INTEGER CHECK
        (approved_provider_revision IS NULL OR approved_provider_revision >= 1);
UPDATE verification_requests SET approved_provider_revision = 1
WHERE provider_id IS NOT NULL AND status IN ('approved', 'running', 'completed');

-- Count each attempted model invocation, including retries, rather than only a request's
-- latest started_utc. This ledger contains no note content or prompt.
CREATE TABLE verification_invocations (
    attempt_id TEXT PRIMARY KEY,
    request_id TEXT NOT NULL REFERENCES verification_requests(request_id) ON DELETE RESTRICT,
    provider_id TEXT NOT NULL REFERENCES verification_providers(provider_id) ON DELETE RESTRICT,
    provider_revision INTEGER NOT NULL CHECK (provider_revision >= 1),
    started_utc TEXT NOT NULL,
    finished_utc TEXT,
    status TEXT NOT NULL CHECK
        (status IN ('running', 'completed', 'released', 'failed', 'provider_changed', 'legacy')),
    failure_code TEXT CHECK (failure_code IS NULL OR length(failure_code) BETWEEN 1 AND 64),
    model_name TEXT,
    model_version TEXT,
    input_tokens INTEGER CHECK (input_tokens IS NULL OR input_tokens >= 0),
    output_tokens INTEGER CHECK (output_tokens IS NULL OR output_tokens >= 0),
    duration_ms INTEGER CHECK (duration_ms IS NULL OR duration_ms >= 0)
) STRICT;
CREATE INDEX verification_invocations_allowance ON
    verification_invocations(provider_id, started_utc);

INSERT INTO verification_invocations
    (attempt_id, request_id, provider_id, provider_revision, started_utc, finished_utc,
     status, failure_code, model_name, model_version, input_tokens, output_tokens, duration_ms)
SELECT request_id, request_id, provider_id, 1, started_utc, completed_utc, 'legacy',
       failure_code, model_name, model_version, input_tokens, output_tokens, duration_ms
FROM verification_requests
WHERE provider_id IS NOT NULL AND started_utc IS NOT NULL;

PRAGMA user_version = 18;
