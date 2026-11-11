ALTER TABLE ingest_requests
    ADD COLUMN revision INTEGER CHECK (revision IS NULL OR revision >= 1);

PRAGMA user_version = 2;
