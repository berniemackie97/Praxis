-- ADR 0043 agent runtime: backend profiles (keys live in the Keychain, never here),
-- conversations and every message, the tool-call audit, knowledge proposals, owner memories
-- and agent-authored artifacts.
CREATE TABLE agent_backends (
    backend_id TEXT PRIMARY KEY CHECK (length(backend_id) BETWEEN 1 AND 64),
    kind TEXT NOT NULL CHECK (kind IN ('openai_compatible', 'anthropic', 'claude_code', 'codex')),
    endpoint TEXT CHECK (endpoint IS NULL OR length(endpoint) BETWEEN 1 AND 512),
    model TEXT NOT NULL CHECK (length(model) BETWEEN 1 AND 200),
    key_account TEXT CHECK (key_account IS NULL OR length(key_account) BETWEEN 1 AND 128),
    executable TEXT CHECK (executable IS NULL OR length(executable) BETWEEN 1 AND 1024),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    updated_utc TEXT NOT NULL
) STRICT;

CREATE TABLE agent_conversations (
    conversation_id TEXT PRIMARY KEY,
    backend_id TEXT NOT NULL CHECK (length(backend_id) BETWEEN 1 AND 64),
    title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
) STRICT;
CREATE INDEX agent_conversations_recent ON agent_conversations(updated_utc DESC);

CREATE TABLE agent_messages (
    conversation_id TEXT NOT NULL REFERENCES agent_conversations(conversation_id) ON DELETE CASCADE,
    sequence INTEGER NOT NULL CHECK (sequence >= 1),
    role TEXT NOT NULL CHECK (role IN ('user', 'assistant', 'tool', 'summary')),
    content TEXT NOT NULL CHECK (length(content) <= 400000),
    tool_calls TEXT NOT NULL DEFAULT '[]' CHECK (length(tool_calls) <= 200000),
    tool_call_id TEXT CHECK (tool_call_id IS NULL OR length(tool_call_id) BETWEEN 1 AND 128),
    created_utc TEXT NOT NULL,
    PRIMARY KEY (conversation_id, sequence)
) STRICT;

CREATE TABLE agent_tool_calls (
    record_id INTEGER PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    call_id TEXT NOT NULL CHECK (length(call_id) BETWEEN 1 AND 128),
    tool TEXT NOT NULL CHECK (length(tool) BETWEEN 1 AND 64),
    tier TEXT NOT NULL CHECK (length(tier) BETWEEN 1 AND 16),
    arguments TEXT NOT NULL CHECK (length(arguments) <= 4000),
    outcome TEXT NOT NULL CHECK (length(outcome) BETWEEN 1 AND 32),
    result_characters INTEGER NOT NULL CHECK (result_characters >= 0),
    citations INTEGER NOT NULL CHECK (citations >= 0),
    withheld INTEGER NOT NULL CHECK (withheld >= 0),
    started_utc TEXT NOT NULL,
    duration_ms INTEGER NOT NULL CHECK (duration_ms >= 0)
) STRICT;
CREATE INDEX agent_tool_calls_conversation ON agent_tool_calls(conversation_id, record_id);

CREATE TABLE agent_proposals (
    proposal_id TEXT PRIMARY KEY,
    conversation_id TEXT,
    kind TEXT NOT NULL CHECK (kind IN ('entity_type', 'entity_rename', 'entity_merge', 'relation', 'source_category')),
    payload TEXT NOT NULL CHECK (length(payload) BETWEEN 2 AND 4000),
    rationale TEXT NOT NULL CHECK (length(rationale) BETWEEN 1 AND 1000),
    status TEXT NOT NULL CHECK (status IN ('pending', 'applied', 'rejected')),
    created_utc TEXT NOT NULL,
    resolved_utc TEXT
) STRICT;
CREATE INDEX agent_proposals_status ON agent_proposals(status, created_utc);

CREATE TABLE agent_memories (
    memory_id TEXT PRIMARY KEY,
    conversation_id TEXT,
    text TEXT NOT NULL CHECK (length(text) BETWEEN 1 AND 300),
    status TEXT NOT NULL CHECK (status IN ('proposed', 'active', 'rejected')),
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL
) STRICT;

CREATE TABLE agent_artifacts (
    artifact_id TEXT PRIMARY KEY,
    title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 80),
    source_id TEXT NOT NULL REFERENCES sources(source_id) ON DELETE RESTRICT,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    path TEXT NOT NULL CHECK (length(path) BETWEEN 1 AND 4096),
    conversation_id TEXT,
    updated_utc TEXT NOT NULL
) STRICT;

PRAGMA user_version = 22;
