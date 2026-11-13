-- ADR 0044. One generic intake engine. Every readable source is triaged (local tier) and
-- understood (strong tier, verified in code, strongest on failure). The financial ledger becomes
-- the transaction profile's projection, produced by the understand stage; the financial-only
-- processors are removed and their derived rows are recomputed by the new stages.

CREATE TABLE intake_documents (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    source_digest TEXT NOT NULL REFERENCES original_objects(digest) ON DELETE RESTRICT,
    format TEXT NOT NULL CHECK (length(format) BETWEEN 1 AND 32),
    pages INTEGER CHECK (pages IS NULL OR pages >= 0),
    title TEXT CHECK (title IS NULL OR length(title) <= 256),
    truncated INTEGER NOT NULL DEFAULT 0 CHECK (truncated IN (0, 1)),
    document_type TEXT CHECK (document_type IS NULL OR length(document_type) BETWEEN 1 AND 64),
    language TEXT CHECK (language IS NULL OR length(language) BETWEEN 2 AND 16),
    gist TEXT CHECK (gist IS NULL OR length(gist) <= 240),
    record_kinds TEXT NOT NULL DEFAULT '' CHECK (length(record_kinds) <= 128),
    triage_tier TEXT CHECK (triage_tier IS NULL OR triage_tier IN ('local', 'hint')),
    summary TEXT CHECK (summary IS NULL OR length(summary) <= 1200),
    status TEXT NOT NULL CHECK (status IN ('read', 'triaged', 'understood', 'partially_verified', 'held', 'index_only', 'skipped', 'failed')),
    reason TEXT CHECK (reason IS NULL OR length(reason) BETWEEN 1 AND 64),
    tier TEXT CHECK (tier IS NULL OR tier IN ('local', 'strong', 'strongest', 'reused')),
    provider_id TEXT CHECK (provider_id IS NULL OR length(provider_id) BETWEEN 1 AND 64),
    model_name TEXT CHECK (model_name IS NULL OR length(model_name) <= 200),
    verified_items INTEGER NOT NULL DEFAULT 0 CHECK (verified_items >= 0),
    rejected_items INTEGER NOT NULL DEFAULT 0 CHECK (rejected_items >= 0),
    input_tokens INTEGER NOT NULL DEFAULT 0 CHECK (input_tokens >= 0),
    output_tokens INTEGER NOT NULL DEFAULT 0 CHECK (output_tokens >= 0),
    updated_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, revision),
    FOREIGN KEY (item_id, revision) REFERENCES item_versions(item_id, revision) ON DELETE RESTRICT
) STRICT;
CREATE INDEX intake_documents_digest ON intake_documents(source_digest, status);

CREATE TABLE intake_records (
    record_id TEXT PRIMARY KEY,
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    processor_key TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('event', 'deadline', 'contact')),
    fields TEXT NOT NULL CHECK (length(fields) BETWEEN 2 AND 4096),
    evidence TEXT NOT NULL CHECK (length(evidence) BETWEEN 1 AND 240),
    evidence_offset INTEGER NOT NULL CHECK (evidence_offset >= 0),
    evidence_length INTEGER NOT NULL CHECK (evidence_length BETWEEN 1 AND 240),
    sort_key TEXT CHECK (sort_key IS NULL OR length(sort_key) BETWEEN 10 AND 16),
    tier TEXT NOT NULL CHECK (tier IN ('local', 'strong', 'strongest', 'reused')),
    provider_id TEXT CHECK (provider_id IS NULL OR length(provider_id) BETWEEN 1 AND 64),
    created_utc TEXT NOT NULL,
    FOREIGN KEY (item_id, revision, processor_key) REFERENCES processing_jobs(item_id, revision, processor_key) ON DELETE CASCADE
) STRICT;
CREATE INDEX intake_records_item ON intake_records(item_id, revision);
CREATE INDEX intake_records_kind_date ON intake_records(kind, sort_key);

-- Column mappings a model produced for a delimited layout, reused by header fingerprint.
CREATE TABLE intake_layouts (
    fingerprint TEXT PRIMARY KEY CHECK (length(fingerprint) = 64),
    mapping TEXT NOT NULL CHECK (length(mapping) BETWEEN 2 AND 4096),
    tier TEXT NOT NULL CHECK (tier IN ('local', 'strong', 'strongest')),
    provider_id TEXT NOT NULL CHECK (length(provider_id) BETWEEN 1 AND 64),
    uses INTEGER NOT NULL DEFAULT 0 CHECK (uses >= 0),
    created_utc TEXT NOT NULL,
    used_utc TEXT NOT NULL
) STRICT;

-- Records of a revoked source are purged; its intake summary and gist are cleared.
CREATE TRIGGER intake_source_revoked AFTER UPDATE OF status ON sources
WHEN NEW.status <> 'active'
BEGIN
    DELETE FROM intake_records WHERE item_id IN (SELECT item_id FROM items WHERE source_id = NEW.source_id);
    UPDATE intake_documents SET summary = NULL, gist = NULL, title = NULL
    WHERE item_id IN (SELECT item_id FROM items WHERE source_id = NEW.source_id);
END;

-- Retire the financial-only processors. Their rows are derived and are recomputed by the understand stage.
DROP TRIGGER financial_records_new_version;
DELETE FROM financial_annotations;
DELETE FROM financial_documents WHERE processor_key IN ('financial-records-v1', 'financial-categories-v1');
DELETE FROM processing_jobs WHERE processor_key IN ('financial-records-v1', 'financial-categories-v1');

DROP TABLE financial_annotations;
CREATE TABLE financial_annotations (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    records_processor TEXT NOT NULL CHECK (records_processor = 'intake-understand-v1'),
    ordinal INTEGER NOT NULL,
    merchant TEXT NOT NULL CHECK (length(merchant) BETWEEN 1 AND 128),
    category TEXT NOT NULL CHECK (category IN ('income', 'interest', 'refund', 'transfer', 'housing', 'utilities', 'groceries',
        'dining', 'transportation', 'shopping', 'subscriptions', 'health', 'insurance', 'entertainment', 'travel', 'fees',
        'cash', 'taxes', 'uncategorized')),
    tier TEXT NOT NULL CHECK (tier IN ('local', 'strong', 'strongest', 'reused')),
    provider_id TEXT NOT NULL CHECK (length(provider_id) BETWEEN 1 AND 64),
    model_name TEXT CHECK (model_name IS NULL OR length(model_name) <= 200),
    created_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, revision, ordinal),
    FOREIGN KEY (item_id, revision, records_processor, ordinal)
        REFERENCES financial_transactions(item_id, revision, processor_key, ordinal) ON DELETE CASCADE
) STRICT;

-- Sources the old text reader could not decode are read again by the new readers.
DELETE FROM text_derivatives
WHERE processor_key = 'source-text-v1' AND outcome = 'unsupported'
  AND reason_code IN ('unsupported_format', 'pdf_not_supported', 'signature_mismatch', 'binary_content')
  AND revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = text_derivatives.item_id);
UPDATE processing_jobs
SET state = 'queued', attempt = 0, lease_key = NULL, lease_until_utc = NULL, failure_code = NULL, updated_utc = created_utc
WHERE processor_key = 'source-text-v1' AND state IN ('unsupported', 'failed')
  AND NOT EXISTS (SELECT 1 FROM text_derivatives d WHERE d.item_id = processing_jobs.item_id
                  AND d.revision = processing_jobs.revision AND d.processor_key = processing_jobs.processor_key)
  AND revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = processing_jobs.item_id)
  AND EXISTS (SELECT 1 FROM items i JOIN sources s ON s.source_id = i.source_id
              WHERE i.item_id = processing_jobs.item_id AND s.status = 'active');

-- Every current, readable revision is triaged (and then understood) once after upgrade.
INSERT INTO processing_jobs (item_id, revision, processor_key, state, attempt, total_attempts, created_utc, updated_utc)
SELECT j.item_id, j.revision, 'intake-triage-v1', 'queued', 0, 0, j.updated_utc, j.updated_utc
FROM processing_jobs j
JOIN items i ON i.item_id = j.item_id
JOIN sources s ON s.source_id = i.source_id
WHERE j.processor_key = 'source-text-v1' AND j.state = 'completed' AND s.status = 'active'
  AND j.revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = j.item_id)
ON CONFLICT(item_id, revision, processor_key) DO NOTHING;

PRAGMA user_version = 23;
