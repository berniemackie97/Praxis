CREATE TABLE agent_chat_recaps (
    conversation_id TEXT PRIMARY KEY REFERENCES agent_conversations(conversation_id) ON DELETE CASCADE,
    pending_through INTEGER NOT NULL CHECK (pending_through > 0),
    reviewed_through INTEGER NOT NULL DEFAULT 0 CHECK (reviewed_through >= 0 AND reviewed_through <= pending_through),
    recap TEXT,
    retry_utc TEXT NOT NULL
) STRICT;

CREATE INDEX agent_chat_recaps_due ON agent_chat_recaps(retry_utc) WHERE reviewed_through < pending_through;

CREATE TABLE agent_identity (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 48)
) STRICT;

PRAGMA user_version = 34;
