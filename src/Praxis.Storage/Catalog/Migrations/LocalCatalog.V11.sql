-- Owner-selected model per processing role. Endpoints are loopback-only (validated before
-- write); remote providers require a separate consent model and are not representable here.
CREATE TABLE model_profiles (
    role TEXT PRIMARY KEY CHECK (role IN ('local-enrichment')),
    provider TEXT NOT NULL CHECK (provider IN ('ollama')),
    endpoint TEXT NOT NULL CHECK (length(endpoint) BETWEEN 1 AND 256),
    model TEXT NOT NULL CHECK (length(model) BETWEEN 1 AND 200),
    context_tokens INTEGER NOT NULL CHECK (context_tokens BETWEEN 2048 AND 262144),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    updated_utc TEXT NOT NULL
) STRICT;

PRAGMA user_version = 11;
