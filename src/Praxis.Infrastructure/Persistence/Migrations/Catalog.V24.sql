-- ADR 0045. Media moments, saved links and code projects.

-- Where each span of a transcript is spoken. Transcripts are stored as readable text; their
-- timing lives here so any character maps back to a moment in the media.
CREATE TABLE text_timings (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    processor_key TEXT NOT NULL CHECK (processor_key = 'source-text-v1'),
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    char_offset INTEGER NOT NULL CHECK (char_offset >= 0),
    char_length INTEGER NOT NULL CHECK (char_length >= 1),
    start_ms INTEGER NOT NULL CHECK (start_ms >= 0),
    end_ms INTEGER NOT NULL CHECK (end_ms >= start_ms),
    PRIMARY KEY (item_id, revision, processor_key, ordinal),
    FOREIGN KEY (item_id, revision, processor_key) REFERENCES processing_jobs(item_id, revision, processor_key) ON DELETE CASCADE
) STRICT, WITHOUT ROWID;

PRAGMA user_version = 24;
