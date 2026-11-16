-- Both the owner review queue and ordinary standing memories use the same approval workflow.
ALTER TABLE agent_memories ADD COLUMN previous_name TEXT;
ALTER TABLE agent_memories ADD COLUMN current_name TEXT;
ALTER TABLE agent_memories ADD COLUMN name_key TEXT;
ALTER TABLE agent_memories ADD COLUMN business_name INTEGER NOT NULL DEFAULT 0 CHECK (business_name IN (0, 1));
CREATE UNIQUE INDEX agent_memory_current_name ON agent_memories(name_key, status)
    WHERE name_key IS NOT NULL AND status IN ('active', 'proposed');

PRAGMA user_version = 38;
