-- Private, bounded watcher fault history. No source contents, exception strings or credentials.
CREATE TABLE intake_incidents (
    incident_id INTEGER PRIMARY KEY,
    root_path TEXT NOT NULL CHECK (length(root_path) BETWEEN 1 AND 4096),
    state TEXT NOT NULL CHECK (state IN ('offline', 'degraded', 'reapproval_required')),
    started_utc TEXT NOT NULL,
    resolved_utc TEXT
) STRICT;
CREATE UNIQUE INDEX intake_incidents_open_root
    ON intake_incidents(root_path) WHERE resolved_utc IS NULL;
CREATE INDEX intake_incidents_recent ON intake_incidents(incident_id DESC);

PRAGMA user_version = 17;
