-- Hands-off intake (ADR 0046). Knowledge the owner sees is what they confirmed, what a verifier
-- supported, or what the strong or strongest tier produced and code verified. Local-model guesses
-- remain candidates (visible_entity_mentions, which verification plans over) but are not
-- knowledge on their own. Markdown headings are document structure, not knowledge: that stage retires.
DROP VIEW IF EXISTS confirmed_entity_mentions;
CREATE VIEW confirmed_entity_mentions AS
SELECT m.*
FROM visible_entity_mentions m
JOIN eligible_enrichment_proposals p ON p.proposal_id = m.proposal_id
WHERE m.status IN ('accepted', 'corrected')
   OR m.verdict = 'supported'
   OR (p.processor_key = 'intake-understand-v1'
       AND EXISTS (SELECT 1 FROM intake_documents d
                   WHERE d.item_id = p.item_id AND d.revision = p.revision
                     AND d.tier IN ('strong', 'strongest', 'reused')));

UPDATE processing_jobs
SET state = 'canceled', lease_key = NULL, lease_until_utc = NULL, failure_code = NULL
WHERE processor_key = 'markdown-heading-v1' AND state IN ('queued', 'running', 'failed');

PRAGMA user_version = 25;
