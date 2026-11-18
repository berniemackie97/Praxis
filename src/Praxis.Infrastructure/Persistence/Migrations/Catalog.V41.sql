-- Keep the exact owner statement beside the existing reviewable memory, not a model paraphrase.
ALTER TABLE agent_memories ADD COLUMN owner_statement TEXT CHECK(owner_statement IS NULL OR length(owner_statement) BETWEEN 1 AND 4000);

-- Indexed derivative of command receipts; deleting the originating chat removes its review cursor.
CREATE TABLE agent_run_reviews (
    run_id TEXT NOT NULL REFERENCES agent_runs(run_id) ON DELETE CASCADE,
    entity_id TEXT NOT NULL,
    fingerprint TEXT NOT NULL,
    PRIMARY KEY(run_id,entity_id)
) STRICT;
CREATE INDEX agent_run_reviews_entity ON agent_run_reviews(entity_id,fingerprint);
INSERT INTO agent_run_reviews(run_id,entity_id,fingerprint)
SELECT r.run_id,json_extract(reviewed.value,'$.entityId'),json_extract(reviewed.value,'$.fingerprint')
FROM agent_runs r,json_each(r.reply,'$.command.reviewed') reviewed
WHERE r.state='completed' AND r.kind='dream';
CREATE INDEX agent_runs_command_history ON agent_runs(conversation_id,completed_utc DESC)
WHERE state='completed' AND json_type(reply,'$.command')='object';

PRAGMA user_version = 41;
