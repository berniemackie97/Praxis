-- Model setup (ADR 0047): unlimited daily allowances (0), a reasoning effort per provider, per-tier
-- model and effort overrides, and an effort per chat backend.
CREATE TABLE verification_providers_v26 (
    provider_id TEXT PRIMARY KEY CHECK (provider_id IN ('ollama', 'codex', 'claude-code')),
    executable TEXT CHECK (executable IS NULL OR length(executable) BETWEEN 1 AND 1024),
    endpoint TEXT CHECK (endpoint IS NULL OR length(endpoint) BETWEEN 1 AND 256),
    model TEXT CHECK (model IS NULL OR length(model) BETWEEN 1 AND 100),
    cost_weight INTEGER NOT NULL CHECK (cost_weight BETWEEN 0 AND 1000),
    daily_limit INTEGER NOT NULL CHECK (daily_limit BETWEEN 0 AND 100000),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    updated_utc TEXT NOT NULL,
    config_revision INTEGER NOT NULL DEFAULT 1 CHECK (config_revision >= 1),
    effort TEXT CHECK (effort IS NULL OR length(effort) BETWEEN 1 AND 16),
    CHECK (
        (provider_id = 'ollama' AND executable IS NULL AND endpoint IS NOT NULL AND model IS NOT NULL)
        OR (provider_id IN ('codex', 'claude-code') AND executable IS NOT NULL AND endpoint IS NULL)
    )
) STRICT;

INSERT INTO verification_providers_v26
    (provider_id, executable, endpoint, model, cost_weight, daily_limit, enabled, updated_utc, config_revision)
SELECT provider_id, executable, endpoint, model, cost_weight, daily_limit, enabled, updated_utc, config_revision
FROM verification_providers;

DROP TABLE verification_providers;
ALTER TABLE verification_providers_v26 RENAME TO verification_providers;

CREATE TABLE model_tiers_v26 (
    tier TEXT PRIMARY KEY CHECK (tier IN ('local', 'strong', 'strongest')),
    provider_id TEXT NOT NULL REFERENCES verification_providers(provider_id) ON DELETE CASCADE,
    updated_utc TEXT NOT NULL,
    model TEXT CHECK (model IS NULL OR length(model) BETWEEN 1 AND 100),
    effort TEXT CHECK (effort IS NULL OR length(effort) BETWEEN 1 AND 16)
) STRICT;
INSERT INTO model_tiers_v26 (tier, provider_id, updated_utc) SELECT tier, provider_id, updated_utc FROM model_tiers;
DROP TABLE model_tiers;
ALTER TABLE model_tiers_v26 RENAME TO model_tiers;

CREATE TABLE agent_backends_v26 (
    backend_id TEXT PRIMARY KEY CHECK (length(backend_id) BETWEEN 1 AND 64),
    kind TEXT NOT NULL CHECK (kind IN ('openai_compatible', 'anthropic', 'claude_code', 'codex')),
    endpoint TEXT CHECK (endpoint IS NULL OR length(endpoint) BETWEEN 1 AND 512),
    model TEXT NOT NULL CHECK (length(model) BETWEEN 1 AND 200),
    key_account TEXT CHECK (key_account IS NULL OR length(key_account) BETWEEN 1 AND 128),
    executable TEXT CHECK (executable IS NULL OR length(executable) BETWEEN 1 AND 1024),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    updated_utc TEXT NOT NULL,
    effort TEXT CHECK (effort IS NULL OR length(effort) BETWEEN 1 AND 16)
) STRICT;
INSERT INTO agent_backends_v26 (backend_id, kind, endpoint, model, key_account, executable, enabled, updated_utc)
SELECT backend_id, kind, endpoint, model, key_account, executable, enabled, updated_utc FROM agent_backends;
DROP TABLE agent_backends;
ALTER TABLE agent_backends_v26 RENAME TO agent_backends;

PRAGMA user_version = 26;
