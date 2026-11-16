-- Chat context is the owner's per-conversation selection, separate from provider and effort.
ALTER TABLE agent_conversations ADD COLUMN context_preset TEXT NOT NULL DEFAULT 'balanced'
    CHECK (context_preset IN ('compact', 'balanced', 'expanded'));

-- Preserve existing action receipts while adding explicitly approved package-manager operations.
CREATE TABLE agent_capability_requests_v37 (
    id TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    operation TEXT NOT NULL CHECK (operation IN ('terminal', 'browser-open', 'brew-install', 'npm-install')),
    target TEXT CHECK (target IS NULL OR length(target) BETWEEN 1 AND 1000),
    description TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('pending', 'running', 'accepted', 'failed', 'rejected', 'expired')),
    detail TEXT CHECK (detail IS NULL OR length(detail) <= 6000),
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
) STRICT;
INSERT INTO agent_capability_requests_v37
    (id, conversation_id, operation, target, description, status, detail, created_utc, updated_utc)
SELECT id, conversation_id, operation, target, description, status, detail, created_utc, updated_utc
FROM agent_capability_requests;
DROP TABLE agent_capability_requests;
ALTER TABLE agent_capability_requests_v37 RENAME TO agent_capability_requests;
CREATE INDEX agent_capability_requests_status ON agent_capability_requests(status, created_utc DESC);

PRAGMA user_version = 37;
