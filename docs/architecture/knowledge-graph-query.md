# Read-only Knowledge Graph projection — #385

## Authority

The graph is a read-only **projection** of the current owner-local SQLite catalog. Canonical entity, relation, proposal, verification, source and revision records remain authoritative. The graph adds no storage, adjacency cache, inference pipeline, SQL migration or network endpoint.

The existing private desktop worker protocol is the only transport. The catalog is protected by the owner-local process boundary and an exclusive owner-private catalog lock. A collection or source filter is **not** a credential. Do not add these actions to a remote or multi-owner transport without a separate identity and scoped authorization design.

## Actions and output

- **graphSeeds** accepts optional search, entityType, collectionId, sourceId, limit and cursor. It returns confirmed matching entities and one eligible source reference per entity.
- **graphNeighborhood** requires entityId, and accepts optional entityType, collectionId, sourceId, predicate, evidenceStatus, limit and cursor. It returns one focused entity and a page of its immediate, authorized relations and neighbors. The entityType filter applies to neighbors, not the focus.
- **graphRelation** requires entityId (an endpoint) and relationId, and accepts the same scope and filters but no cursor. It returns a single relationship and its currently visible endpoints, or unavailable without exposing hidden names. It reuses the neighborhood SQL/visibility rules and rechecks authorization at inspection time.

Each page is bounded to 1–100 edges or seed nodes (default 40). Suggested nodes and edges require explicit includeSuggestions=true. The evidenceStatus filter accepts owner, accepted, verified, documented, suggested, or contradicted. The last two require explicit suggestion-mode opt-in; contradicted means an actual recorded unsupported verifier verdict, not conflicting predicates guessed from the drawing. The response includes stable canonical entity and relation IDs, real predicates and direction, visible labels, owner or evidence origins, review states, current scoped mention/source counts, and actions for opening the canonical inspector. Node and edge evidence includes the exact eligible proposal, source, item, revision, digest, locator and span. Owner-authored relations have no invented document evidence.

The result outcome is ready, empty or unavailable. Invalid and over-limit requests are rejected by the private protocol.

## Visibility rules

By default, only active entities with **confirmed, current, in-scope** source mentions are returned. With explicit includeSuggestions=true, eligible pending model mentions from visible_entity_mentions can also appear as suggested or mixed, not as confirmed. Rejected, revoked, superseded and withheld instruction-shaped proposals remain hidden. Even an owner type override cannot keep revoked content visible.

The existing confirmed_entity_mentions view controls default node visibility. Explicit suggestion mode uses visible_entity_mentions, with distinct trust states. An inferred node type and label come from evidence eligible in the selected trust mode; an explicit owner rename or type decision takes precedence. When suggestions are included, node status is suggested, mixed or confirmed, and example evidence prefers a confirmed citation. Unreviewed machine aliases cannot make a search result match; an owner-authored alias may match a currently visible entity.

By default, a model relation needs an eligible current source proposal and confirmed evidence: owner acceptance/correction, supporting verifier verdict, or the existing confirmed strong/strongest intake path. With includeSuggestions=true, ordinary pending model relations can appear as suggested and eligible relations with an explicit unsupported verifier verdict as contradicted. Rejected and unapproved instruction-shaped edges remain hidden. Multiple evidence observations with the same endpoints remain distinct; differing predicates are not automatically contradictions. Instruction-shaped evidence has stricter review rules. An owner-authored relation stays an owner assertion with no invented source proof. Both endpoints must be visible within the current source/collection scope before its row can enter pagination.

A confirmed relation is not inferred from a co-mention, proximity, similarity score or graph position. There is no multihop expansion, client SQL, automatic approval or write action.

Collection membership must be active; revoked sources, obsolete revisions and rejected proposals are excluded by current canonical records. No new graph cache needs invalidation after lifecycle writes.

## Evidence inspection and review — #387

The macOS inspector performs a scoped `graphRelation` re-read before exposing a selected edge or invoking source and review actions. It does not treat an old SpriteKit selection as authority. Source navigation revalidates again and uses an exact source/item/revision/digest match when a Library item is available. Otherwise the existing `preview` action returns up to 12,000 characters of current active text, and the client highlights only an exact quote match. Missing, revised, revoked, unanchorable or unsupported PDF/media selectors produce a visible unavailable outcome; the source filesystem locator is not a page/row/timecode locator.

An owner can accept or reject a currently eligible source-backed relation through the existing `topicReview` action. This action applies the repository's current-source eligibility and review history; graph gestures or agent output never mutate graph state. Owner-authored edges carry no proposal or invented citation and are not presented as reviewable proposals. Entity corrections remain in the Entities workspace. The explicit Ask Praxis action only prepares a draft of current, revalidated relation identifiers, with no source excerpt or automatic model call; the owner chooses the model and sends it through the existing chat/disclosure workflow.

Selection changes, focus changes, scope changes, refresh and view deactivation invalidate scoped replies and visible inspection text. A newly unavailable relationship triggers a fresh graph projection. This is a current-read guard, not a continuous subscription to changes; the measured revocation-race and accessibility release gate remains #388.

## Cursor and cancellation

Pages use stable entity or relation ID ordering. SQL applies visibility and evidence policy before LIMIT, and reads at most limit + 1 rows to identify another page. Hidden relationships cannot affect the returned continuation hint.

The cursor binds the query mode, scope, filter and focus to the worker session and the current catalog writer change count. A catalog write or worker restart invalidates it. The cursor is not an access token. This conservative version fence may reject a page after an unrelated background write; the caller must restart the query rather than continue against mixed evidence.

Reads run through the existing serialized catalog gate so concurrent canonical mutations cannot interleave inside one page. Cancellation is checked before reading and while materializing results; graph queries do not mutate catalog state.

## Implementation and acceptance boundaries

The graph projects entity/relation knowledge, not arbitrary file co-occurrence. One sample of confirmed source evidence is returned per node; the existing evidence inspector supplies further source detail. No graph-wide total is exposed. The source and mention counts are confined to current eligible evidence within the selected scope and selected trust mode.

Issue #386 owns the SpriteKit scene, layout, viewport budgets, focused neighborhood expansion, semantic zoom and keyboard-accessible navigation. Issue #387 owns evidence inspection and review interactions. Issue #388 owns measured production macOS performance, accessibility, resource limits and revocation acceptance.

Verify the .NET 10 locked restore, Release build, test suite, formatting, negative security cases, desktop protocol and current-head CI before closing #385. The code alone is not evidence that all acceptance gates have passed.
