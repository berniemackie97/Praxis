-- Keep model and reasoning effort fixed for a conversation even when its shared provider profile changes.
ALTER TABLE agent_conversations ADD COLUMN model TEXT CHECK (model IS NULL OR length(model) BETWEEN 1 AND 200);
ALTER TABLE agent_conversations ADD COLUMN effort TEXT CHECK (effort IS NULL OR length(effort) BETWEEN 1 AND 16);

-- Snapshot existing chat settings where the original provider is still configured.
-- Missing profiles retain NULL, so a legacy chat can resume if its provider is reconnected.
UPDATE agent_conversations
SET model = (SELECT b.model FROM agent_backends b WHERE b.backend_id = agent_conversations.backend_id),
    effort = (SELECT b.effort FROM agent_backends b WHERE b.backend_id = agent_conversations.backend_id);

PRAGMA user_version = 31;
