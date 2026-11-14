-- Claude Code and Codex calls failed on Praxis's side (structured output was blocked for Claude
-- Code; Codex rejected a non-strict schema). Read the files those failures left unread again.
UPDATE processing_jobs
SET state = 'queued', attempt = 0, lease_key = NULL, lease_until_utc = NULL, failure_code = NULL,
    updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
WHERE processor_key = 'intake-understand-v1' AND state = 'completed'
  AND EXISTS (SELECT 1 FROM intake_documents d
              WHERE d.item_id = processing_jobs.item_id AND d.revision = processing_jobs.revision
                AND d.status = 'index_only' AND d.reason = 'model_invalid_response');

-- How long each model call took, for the owner's Activity view.
CREATE TABLE model_invocations_v29 (
    invocation_id INTEGER PRIMARY KEY,
    tier TEXT NOT NULL CHECK (tier IN ('local', 'strong', 'strongest')),
    provider_id TEXT NOT NULL CHECK (length(provider_id) BETWEEN 1 AND 64),
    remote INTEGER NOT NULL CHECK (remote IN (0, 1)),
    purpose TEXT NOT NULL CHECK (purpose IN ('intake', 'chat')),
    category TEXT NOT NULL,
    decision_reason TEXT NOT NULL CHECK (length(decision_reason) BETWEEN 1 AND 64),
    item_id TEXT,
    revision INTEGER,
    processor_key TEXT,
    model_name TEXT CHECK (model_name IS NULL OR length(model_name) <= 200),
    input_tokens INTEGER NOT NULL CHECK (input_tokens >= 0),
    output_tokens INTEGER NOT NULL CHECK (output_tokens >= 0),
    outcome TEXT NOT NULL CHECK (length(outcome) BETWEEN 1 AND 64),
    started_utc TEXT NOT NULL,
    duration_ms INTEGER CHECK (duration_ms IS NULL OR duration_ms >= 0)
) STRICT;

INSERT INTO model_invocations_v29
    (invocation_id, tier, provider_id, remote, purpose, category, decision_reason, item_id, revision, processor_key,
     model_name, input_tokens, output_tokens, outcome, started_utc)
SELECT invocation_id, tier, provider_id, remote, purpose, category, decision_reason, item_id, revision, processor_key,
       model_name, input_tokens, output_tokens, outcome, started_utc
FROM model_invocations;

DROP TABLE model_invocations;
ALTER TABLE model_invocations_v29 RENAME TO model_invocations;
CREATE INDEX IF NOT EXISTS model_invocations_provider_day ON model_invocations(provider_id, started_utc);

PRAGMA user_version = 29;
