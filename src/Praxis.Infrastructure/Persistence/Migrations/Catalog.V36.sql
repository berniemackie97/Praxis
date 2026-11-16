-- Verified owner decisions and local execution receipts for Chat capabilities.
CREATE TABLE agent_capability_requests (
    id TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    operation TEXT NOT NULL CHECK (operation IN ('terminal', 'browser-open')),
    target TEXT CHECK (target IS NULL OR length(target) BETWEEN 1 AND 1000),
    description TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('pending', 'running', 'accepted', 'failed', 'rejected', 'expired')),
    detail TEXT CHECK (detail IS NULL OR length(detail) <= 6000),
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
) STRICT;
CREATE INDEX agent_capability_requests_status ON agent_capability_requests(status, created_utc DESC);

ALTER TABLE agent_messages ADD COLUMN citations TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(citations));

CREATE TABLE agent_brain_recalls (
    id TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL REFERENCES agent_conversations(conversation_id) ON DELETE CASCADE,
    receipt TEXT NOT NULL CHECK (json_valid(receipt))
) STRICT;
CREATE INDEX agent_brain_recalls_conversation ON agent_brain_recalls (conversation_id);

PRAGMA user_version = 36;
