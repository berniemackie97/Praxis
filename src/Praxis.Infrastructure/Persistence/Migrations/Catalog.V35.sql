-- Owner-reviewed, text-only procedural memory. All revisions are retained unchanged.
CREATE TABLE agent_skill_revisions (
    revision_id TEXT PRIMARY KEY,
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 64),
    description TEXT NOT NULL CHECK (length(description) BETWEEN 1 AND 1024),
    markdown TEXT NOT NULL CHECK (length(markdown) BETWEEN 1 AND 6000),
    digest TEXT NOT NULL CHECK (length(digest) = 64),
    base_revision_id TEXT,
    conversation_id TEXT,
    origin TEXT NOT NULL CHECK (origin IN ('chat', 'import')),
    rationale TEXT NOT NULL CHECK (length(rationale) BETWEEN 1 AND 1000),
    state TEXT NOT NULL CHECK (state IN ('pending', 'accepted', 'rejected')),
    created_utc TEXT NOT NULL,
    decided_utc TEXT,
    UNIQUE (name, revision_id),
    FOREIGN KEY (name, base_revision_id) REFERENCES agent_skill_revisions (name, revision_id),
    CHECK ((state = 'pending') = (decided_utc IS NULL))
) STRICT;
CREATE INDEX agent_skill_revisions_state ON agent_skill_revisions (state, created_utc DESC);
CREATE INDEX agent_skill_revisions_history ON agent_skill_revisions (name, created_utc DESC);

CREATE TABLE agent_skill_installs (
    name TEXT PRIMARY KEY,
    revision_id TEXT NOT NULL,
    FOREIGN KEY (name, revision_id) REFERENCES agent_skill_revisions (name, revision_id)
) STRICT;

-- Also supports disabling application-owned skills.
CREATE TABLE agent_skill_disabled (
    name TEXT PRIMARY KEY CHECK (length(name) BETWEEN 1 AND 64)
) STRICT;

PRAGMA user_version = 35;
