-- Stronger-model verification. Nothing is sent until the owner approves a request; the stored
-- payload is exactly what is sent. Verdicts act on the machine layer and never override owner
-- review decisions.
CREATE TABLE verification_providers (
    provider_id TEXT PRIMARY KEY CHECK (provider_id IN ('codex', 'claude-code')),
    executable TEXT NOT NULL CHECK (length(executable) BETWEEN 1 AND 1024),
    model TEXT CHECK (model IS NULL OR length(model) BETWEEN 1 AND 100),
    cost_weight INTEGER NOT NULL CHECK (cost_weight BETWEEN 0 AND 1000),
    daily_limit INTEGER NOT NULL CHECK (daily_limit BETWEEN 1 AND 500),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    updated_utc TEXT NOT NULL
) STRICT;

CREATE TABLE verification_requests (
    request_id TEXT PRIMARY KEY,
    reason TEXT NOT NULL CHECK (reason IN ('instruction_like', 'type_conflict', 'owner_request')),
    status TEXT NOT NULL CHECK (status IN
        ('awaiting_approval', 'approved', 'running', 'completed', 'declined', 'failed')),
    provider_id TEXT REFERENCES verification_providers(provider_id) ON DELETE RESTRICT,
    payload TEXT NOT NULL CHECK (length(payload) BETWEEN 2 AND 65536),
    created_utc TEXT NOT NULL,
    decided_utc TEXT,
    started_utc TEXT,
    completed_utc TEXT,
    retry_after_utc TEXT,
    failure_code TEXT CHECK (failure_code IS NULL OR length(failure_code) BETWEEN 1 AND 64),
    model_name TEXT,
    model_version TEXT,
    input_tokens INTEGER CHECK (input_tokens IS NULL OR input_tokens >= 0),
    output_tokens INTEGER CHECK (output_tokens IS NULL OR output_tokens >= 0),
    duration_ms INTEGER CHECK (duration_ms IS NULL OR duration_ms >= 0),
    lease_key TEXT,
    lease_until_utc TEXT,
    CHECK ((status = 'running') = (lease_key IS NOT NULL AND lease_until_utc IS NOT NULL)),
    CHECK (status NOT IN ('approved', 'running', 'completed') OR provider_id IS NOT NULL)
) STRICT;

CREATE INDEX verification_requests_status ON verification_requests(status, created_utc);

CREATE TABLE verification_items (
    request_id TEXT NOT NULL REFERENCES verification_requests(request_id) ON DELETE RESTRICT,
    ordinal INTEGER NOT NULL CHECK (ordinal >= 1),
    proposal_id TEXT REFERENCES enrichment_proposals(proposal_id) ON DELETE RESTRICT,
    entity_id TEXT REFERENCES knowledge_entities(entity_id) ON DELETE RESTRICT,
    included INTEGER NOT NULL CHECK (included IN (0, 1)),
    verdict TEXT CHECK (verdict IS NULL OR verdict IN ('supported', 'unsupported', 'uncertain')),
    corrected_type TEXT CHECK (corrected_type IS NULL OR corrected_type IN
        ('person', 'organization', 'project', 'technology', 'concept', 'place',
         'document', 'event', 'other')),
    note TEXT CHECK (note IS NULL OR length(note) <= 300),
    PRIMARY KEY (request_id, ordinal),
    CHECK ((proposal_id IS NULL) <> (entity_id IS NULL))
) STRICT;

CREATE INDEX verification_items_proposal ON verification_items(proposal_id);
CREATE INDEX verification_items_entity ON verification_items(entity_id);

-- The most recent completed verdict for each proposal.
CREATE VIEW proposal_verdicts AS
SELECT i.proposal_id, i.verdict
FROM verification_items i
JOIN verification_requests r ON r.request_id = i.request_id
WHERE r.status = 'completed' AND i.proposal_id IS NOT NULL AND i.verdict IS NOT NULL
  AND r.completed_utc = (
      SELECT MAX(r2.completed_utc) FROM verification_items i2
      JOIN verification_requests r2 ON r2.request_id = i2.request_id
      WHERE r2.status = 'completed' AND i2.proposal_id = i.proposal_id AND i2.verdict IS NOT NULL);

-- Owner decisions win; otherwise a verifier's "unsupported" hides a mention and its
-- "supported" releases one withheld for instruction-like evidence.
DROP VIEW visible_entity_mentions;
CREATE VIEW visible_entity_mentions AS
SELECT m.entity_id, p.proposal_id, p.item_id, p.revision, p.source_id, p.entity_type,
       p.evidence, p.evidence_offset, p.status, p.flags, v.verdict
FROM entity_mentions m
JOIN eligible_enrichment_proposals p ON p.proposal_id = m.proposal_id
LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
WHERE p.status <> 'rejected'
  AND (p.status IN ('accepted', 'corrected')
       OR (COALESCE(v.verdict, '') <> 'unsupported'
           AND (p.flags IS NULL OR v.verdict = 'supported')));

PRAGMA user_version = 14;
