-- ADR 0043. Model tiers, data categories, disclosure policy, holds and the invocation audit;
-- typed financial facts from bank and card documents with model annotations. Financial rows
-- are bound to the exact revision digest that produced them and are visible only while the
-- source is active, the revision current and the job completed. Revoking a source purges its
-- financial rows. Full account numbers are never stored.

CREATE TABLE model_tiers (
    tier TEXT PRIMARY KEY CHECK (tier IN ('local', 'strong', 'strongest')),
    provider_id TEXT NOT NULL REFERENCES verification_providers(provider_id) ON DELETE CASCADE,
    updated_utc TEXT NOT NULL
) STRICT;

CREATE TABLE disclosure_settings (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    mode TEXT NOT NULL CHECK (mode IN ('local_only', 'ask_each_time', 'standing_grants', 'allow_all')),
    tool_permission TEXT NOT NULL CHECK (tool_permission IN ('read_only', 'propose_edits', 'allow_edits')),
    hold_hours INTEGER NOT NULL CHECK (hold_hours BETWEEN 1 AND 720),
    updated_utc TEXT NOT NULL
) STRICT;

CREATE TABLE disclosure_rules (
    rule_id TEXT PRIMARY KEY,
    category TEXT NOT NULL CHECK (category IN ('financial', 'correspondence', 'health', 'identity', 'personal', 'documents', 'media', 'code', 'unclassified')),
    provider_id TEXT NOT NULL CHECK (length(provider_id) BETWEEN 1 AND 64),
    purpose TEXT NOT NULL CHECK (purpose IN ('intake', 'chat')),
    allow INTEGER NOT NULL CHECK (allow IN (0, 1)),
    expires_utc TEXT,
    created_utc TEXT NOT NULL
) STRICT;

CREATE TABLE disclosure_fallbacks (
    category TEXT PRIMARY KEY CHECK (category IN ('financial', 'correspondence', 'health', 'identity', 'personal', 'documents', 'media', 'code', 'unclassified')),
    chain TEXT NOT NULL CHECK (length(chain) BETWEEN 1 AND 128),
    updated_utc TEXT NOT NULL
) STRICT;

CREATE TABLE policy_changes (
    change_id TEXT PRIMARY KEY,
    payload TEXT NOT NULL CHECK (length(payload) BETWEEN 2 AND 4096),
    requested_by TEXT NOT NULL CHECK (requested_by IN ('owner', 'agent')),
    rationale TEXT NOT NULL CHECK (length(rationale) <= 500),
    status TEXT NOT NULL CHECK (status IN ('pending', 'applied', 'rejected')),
    requested_utc TEXT NOT NULL,
    resolved_utc TEXT
) STRICT;
CREATE INDEX policy_changes_pending ON policy_changes(status, requested_utc);

CREATE TABLE source_categories (
    source_id TEXT PRIMARY KEY REFERENCES sources(source_id) ON DELETE RESTRICT,
    category TEXT NOT NULL CHECK (category IN ('financial', 'correspondence', 'health', 'identity', 'personal', 'documents', 'media', 'code', 'unclassified')),
    origin TEXT NOT NULL CHECK (origin IN ('hint', 'model', 'owner')),
    local_only INTEGER NOT NULL CHECK (local_only IN (0, 1)),
    updated_utc TEXT NOT NULL
) STRICT;

CREATE TABLE disclosure_holds (
    hold_id TEXT PRIMARY KEY,
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    processor_key TEXT NOT NULL,
    category TEXT NOT NULL,
    provider_id TEXT NOT NULL,
    purpose TEXT NOT NULL CHECK (purpose IN ('intake', 'chat')),
    status TEXT NOT NULL CHECK (status IN ('open', 'approved_once', 'declined', 'expired')),
    deadline_utc TEXT NOT NULL,
    created_utc TEXT NOT NULL,
    decided_utc TEXT,
    FOREIGN KEY (item_id, revision, processor_key) REFERENCES processing_jobs(item_id, revision, processor_key) ON DELETE CASCADE
) STRICT;
CREATE INDEX disclosure_holds_job ON disclosure_holds(item_id, revision, processor_key, provider_id);
CREATE INDEX disclosure_holds_open ON disclosure_holds(status, deadline_utc);

CREATE TABLE model_invocations (
    invocation_id INTEGER PRIMARY KEY,
    tier TEXT NOT NULL CHECK (tier IN ('local', 'strong', 'strongest')),
    provider_id TEXT NOT NULL CHECK (length(provider_id) BETWEEN 1 AND 64),
    remote INTEGER NOT NULL CHECK (remote IN (0, 1)),
    purpose TEXT NOT NULL CHECK (purpose IN ('intake', 'chat')),
    category TEXT NOT NULL,
    decision_reason TEXT NOT NULL CHECK (length(decision_reason) BETWEEN 1 AND 64),
    item_id TEXT,
    revision INTEGER,
    processor_key TEXT,
    model_name TEXT CHECK (model_name IS NULL OR length(model_name) <= 200),
    input_tokens INTEGER NOT NULL CHECK (input_tokens >= 0),
    output_tokens INTEGER NOT NULL CHECK (output_tokens >= 0),
    outcome TEXT NOT NULL CHECK (length(outcome) BETWEEN 1 AND 64),
    started_utc TEXT NOT NULL
) STRICT;
CREATE INDEX model_invocations_provider_day ON model_invocations(provider_id, started_utc);
CREATE TABLE financial_documents (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK (revision >= 1),
    processor_key TEXT NOT NULL,
    source_digest TEXT NOT NULL REFERENCES original_objects(digest) ON DELETE RESTRICT,
    outcome TEXT NOT NULL CHECK (outcome IN ('completed', 'unsupported')),
    format TEXT CHECK (format IS NULL OR format IN ('ofx', 'qif', 'delimited', 'pdf_statement')),
    parser TEXT CHECK (parser IS NULL OR length(parser) BETWEEN 1 AND 64),
    reason_code TEXT CHECK (reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 64),
    issues TEXT NOT NULL DEFAULT '' CHECK (length(issues) <= 1024),
    tier TEXT CHECK (tier IS NULL OR tier IN ('local', 'strong', 'strongest')),
    model_provider TEXT CHECK (model_provider IS NULL OR length(model_provider) BETWEEN 1 AND 64),
    model_name TEXT CHECK (model_name IS NULL OR length(model_name) BETWEEN 1 AND 200),
    model_digest TEXT CHECK (model_digest IS NULL OR length(model_digest) BETWEEN 1 AND 256),
    created_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, revision, processor_key),
    FOREIGN KEY (item_id, revision, processor_key)
        REFERENCES processing_jobs(item_id, revision, processor_key) ON DELETE RESTRICT,
    CHECK ((outcome = 'completed' AND format IS NOT NULL AND parser IS NOT NULL AND reason_code IS NULL)
        OR (outcome = 'unsupported' AND format IS NULL AND reason_code IS NOT NULL))
) STRICT;

CREATE TABLE financial_accounts (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    processor_key TEXT NOT NULL,
    account_key TEXT NOT NULL CHECK (length(account_key) BETWEEN 1 AND 64),
    kind TEXT NOT NULL CHECK (kind IN ('checking', 'savings', 'credit_card', 'loan', 'investment', 'unknown')),
    mask TEXT CHECK (mask IS NULL OR length(mask) BETWEEN 1 AND 16),
    institution TEXT CHECK (institution IS NULL OR length(institution) BETWEEN 1 AND 128),
    currency TEXT NOT NULL CHECK (length(currency) = 3),
    display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 128),
    PRIMARY KEY (item_id, revision, processor_key, account_key),
    FOREIGN KEY (item_id, revision, processor_key)
        REFERENCES financial_documents(item_id, revision, processor_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE financial_statements (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    processor_key TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    account_key TEXT NOT NULL,
    period_start TEXT,
    period_end TEXT,
    opening_minor INTEGER,
    closing_minor INTEGER,
    currency TEXT NOT NULL CHECK (length(currency) = 3),
    verification TEXT NOT NULL CHECK (verification IN ('balanced', 'running_balance', 'provider_export', 'unverified')),
    verification_note TEXT CHECK (verification_note IS NULL OR length(verification_note) BETWEEN 1 AND 64),
    PRIMARY KEY (item_id, revision, processor_key, ordinal),
    FOREIGN KEY (item_id, revision, processor_key, account_key)
        REFERENCES financial_accounts(item_id, revision, processor_key, account_key) ON DELETE CASCADE
) STRICT;

CREATE TABLE financial_transactions (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    processor_key TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
    account_key TEXT NOT NULL,
    statement_ordinal INTEGER,
    posted_on TEXT NOT NULL CHECK (length(posted_on) = 10),
    amount_minor INTEGER NOT NULL,
    currency TEXT NOT NULL CHECK (length(currency) = 3),
    description TEXT NOT NULL CHECK (length(description) BETWEEN 1 AND 512),
    status TEXT NOT NULL CHECK (status IN ('posted', 'pending')),
    provider_type TEXT CHECK (provider_type IS NULL OR length(provider_type) BETWEEN 1 AND 64),
    provider_id TEXT CHECK (provider_id IS NULL OR length(provider_id) BETWEEN 1 AND 255),
    check_number TEXT CHECK (check_number IS NULL OR length(check_number) BETWEEN 1 AND 32),
    provider_category TEXT CHECK (provider_category IS NULL OR length(provider_category) BETWEEN 1 AND 128),
    balance_minor INTEGER,
    locator TEXT NOT NULL CHECK (length(locator) BETWEEN 1 AND 128),
    origin TEXT NOT NULL CHECK (origin IN ('parser', 'model')),
    PRIMARY KEY (item_id, revision, processor_key, ordinal),
    FOREIGN KEY (item_id, revision, processor_key, account_key)
        REFERENCES financial_accounts(item_id, revision, processor_key, account_key) ON DELETE CASCADE,
    FOREIGN KEY (item_id, revision, processor_key, statement_ordinal)
        REFERENCES financial_statements(item_id, revision, processor_key, ordinal) ON DELETE CASCADE
) STRICT;

CREATE INDEX financial_transactions_account_date ON financial_transactions(account_key, posted_on);

-- Model understanding of recorded rows; provider facts are never changed.
CREATE TABLE financial_annotations (
    item_id TEXT NOT NULL,
    revision INTEGER NOT NULL,
    records_processor TEXT NOT NULL CHECK (records_processor = 'financial-records-v1'),
    ordinal INTEGER NOT NULL,
    merchant TEXT NOT NULL CHECK (length(merchant) BETWEEN 1 AND 128),
    category TEXT NOT NULL CHECK (category IN ('income', 'interest', 'refund', 'transfer', 'housing', 'utilities', 'groceries',
        'dining', 'transportation', 'shopping', 'subscriptions', 'health', 'insurance', 'entertainment', 'travel', 'fees',
        'cash', 'taxes', 'uncategorized')),
    tier TEXT NOT NULL CHECK (tier IN ('local', 'strong', 'strongest')),
    provider_id TEXT NOT NULL CHECK (length(provider_id) BETWEEN 1 AND 64),
    model_name TEXT CHECK (model_name IS NULL OR length(model_name) <= 200),
    created_utc TEXT NOT NULL,
    PRIMARY KEY (item_id, revision, ordinal),
    FOREIGN KEY (item_id, revision, records_processor, ordinal)
        REFERENCES financial_transactions(item_id, revision, processor_key, ordinal) ON DELETE CASCADE
) STRICT;

-- Documents whose rows may be read: completed, digest-matched, latest revision of an active source.
CREATE VIEW eligible_financial_documents AS
SELECT d.item_id, d.revision, d.processor_key, d.format, d.source_digest, s.source_id, s.locator
FROM financial_documents d
JOIN processing_jobs j ON j.item_id = d.item_id AND j.revision = d.revision
    AND j.processor_key = d.processor_key AND j.state = 'completed'
JOIN item_versions v ON v.item_id = d.item_id AND v.revision = d.revision AND v.digest = d.source_digest
JOIN items i ON i.item_id = d.item_id
JOIN sources s ON s.source_id = i.source_id
WHERE d.outcome = 'completed' AND s.status = 'active'
  AND v.revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = v.item_id);

-- Schedule recognition for candidate financial file types at capture, inside the capture transaction.
CREATE TRIGGER financial_records_new_version AFTER INSERT ON item_versions
WHEN EXISTS (
    SELECT 1 FROM items i JOIN sources s ON s.source_id = i.source_id
    WHERE i.item_id = NEW.item_id AND s.source_kind = 'file' AND s.status = 'active'
      AND (lower(s.locator) LIKE '%.ofx' OR lower(s.locator) LIKE '%.qfx' OR lower(s.locator) LIKE '%.qbo'
        OR lower(s.locator) LIKE '%.qif' OR lower(s.locator) LIKE '%.csv' OR lower(s.locator) LIKE '%.tsv'
        OR lower(s.locator) LIKE '%.pdf'))
BEGIN
    INSERT INTO processing_jobs
        (item_id, revision, processor_key, state, attempt, total_attempts, created_utc, updated_utc)
    VALUES (NEW.item_id, NEW.revision, 'financial-records-v1', 'queued', 0, 0, NEW.observed_utc, NEW.observed_utc)
    ON CONFLICT(item_id, revision, processor_key) DO NOTHING;
END;

-- Financial rows are sensitive: a revoked source keeps only its outcome record, not its rows.
CREATE TRIGGER financial_records_source_revoked AFTER UPDATE OF status ON sources
WHEN NEW.status <> 'active'
BEGIN
    DELETE FROM financial_accounts WHERE item_id IN (SELECT item_id FROM items WHERE source_id = NEW.source_id);
END;

-- Existing captures of candidate types are recognized once after upgrade.
INSERT INTO processing_jobs
    (item_id, revision, processor_key, state, attempt, total_attempts, created_utc, updated_utc)
SELECT v.item_id, v.revision, 'financial-records-v1', 'queued', 0, 0, v.observed_utc, v.observed_utc
FROM item_versions v
JOIN items i ON i.item_id = v.item_id
JOIN sources s ON s.source_id = i.source_id
WHERE s.source_kind = 'file' AND s.status = 'active'
  AND v.revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = v.item_id)
  AND (lower(s.locator) LIKE '%.ofx' OR lower(s.locator) LIKE '%.qfx' OR lower(s.locator) LIKE '%.qbo'
    OR lower(s.locator) LIKE '%.qif' OR lower(s.locator) LIKE '%.csv' OR lower(s.locator) LIKE '%.tsv'
    OR lower(s.locator) LIKE '%.pdf')
ON CONFLICT(item_id, revision, processor_key) DO NOTHING;

PRAGMA user_version = 21;
