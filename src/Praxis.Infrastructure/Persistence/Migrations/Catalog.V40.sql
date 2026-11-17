CREATE TABLE agent_runs (
    run_id TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL REFERENCES agent_conversations(conversation_id) ON DELETE CASCADE,
    request TEXT NOT NULL CHECK(length(request) BETWEEN 1 AND 32000),
    use_tools INTEGER NOT NULL CHECK(use_tools IN (0,1)),
    kind TEXT NOT NULL CHECK(kind IN ('chat','dream')),
    state TEXT NOT NULL CHECK(state IN ('queued','running','completed','failed','canceled')),
    backend_id TEXT NOT NULL,
    backend_config TEXT NOT NULL CHECK(json_valid(backend_config)),
    model TEXT,
    effort TEXT,
    attempt INTEGER NOT NULL DEFAULT 0,
    message_start INTEGER,
    message_end INTEGER,
    reply TEXT CHECK(reply IS NULL OR json_valid(reply)),
    error TEXT,
    created_utc TEXT NOT NULL,
    started_utc TEXT,
    completed_utc TEXT,
    learning_state TEXT NOT NULL DEFAULT 'ineligible',
    learning_attempt INTEGER NOT NULL DEFAULT 0,
    learning_started_utc TEXT,
    learning_reviewed_utc TEXT,
    learning_skill TEXT,
    learning_input INTEGER NOT NULL DEFAULT 0,
    learning_output INTEGER NOT NULL DEFAULT 0,
    learning_model TEXT
) STRICT;
CREATE INDEX agent_runs_pending ON agent_runs(state,created_utc);
CREATE INDEX agent_runs_conversation ON agent_runs(conversation_id,state);
CREATE INDEX agent_runs_learning ON agent_runs(learning_state,completed_utc);
CREATE TABLE agent_run_events (
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    sequence INTEGER NOT NULL,
    kind TEXT NOT NULL,
    tool TEXT,
    detail TEXT,
    at_utc TEXT NOT NULL,
    PRIMARY KEY(run_id,sequence)
) STRICT;
CREATE TABLE processing_controls (
    processor_key TEXT PRIMARY KEY,
    paused INTEGER NOT NULL CHECK(paused IN (0,1)),
    actor TEXT NOT NULL,
    changed_utc TEXT NOT NULL
) STRICT;
CREATE TABLE agent_skill_experiences (
    revision_id TEXT NOT NULL REFERENCES agent_skill_revisions(revision_id) ON DELETE CASCADE,
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    PRIMARY KEY(revision_id,run_id)
) STRICT;
CREATE TABLE agent_learning_attempts (
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    attempt INTEGER NOT NULL,
    started_utc TEXT NOT NULL,
    input_tokens INTEGER NOT NULL DEFAULT 0,
    output_tokens INTEGER NOT NULL DEFAULT 0,
    model TEXT,
    outcome TEXT,
    PRIMARY KEY(run_id,attempt)
) STRICT;
ALTER TABLE agent_skill_revisions ADD COLUMN evidence_invalidated INTEGER NOT NULL DEFAULT 0 CHECK(evidence_invalidated IN (0,1));
CREATE TRIGGER agent_run_forget_skills BEFORE DELETE ON agent_runs BEGIN
    UPDATE agent_skill_revisions SET evidence_invalidated=1
    WHERE revision_id IN (SELECT revision_id FROM agent_skill_experiences WHERE run_id=OLD.run_id);
END;
ALTER TABLE link_settings ADD COLUMN study_media INTEGER NOT NULL DEFAULT 0 CHECK(study_media IN (0,1));
ALTER TABLE link_settings ADD COLUMN sample_frames INTEGER NOT NULL DEFAULT 0 CHECK(sample_frames IN (0,1));
ALTER TABLE link_settings ADD COLUMN vision_model TEXT;
ALTER TABLE link_settings ADD COLUMN maximum_media_minutes INTEGER NOT NULL DEFAULT 60 CHECK(maximum_media_minutes BETWEEN 1 AND 240);
PRAGMA user_version = 40;
