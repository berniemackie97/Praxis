# ADR 0054: Conversation deletion and independent knowledge

**Status:** Proposed — [#432](https://github.com/berniemackie97/Praxis/issues/432), dependent on [#431](https://github.com/berniemackie97/Praxis/issues/431).

## Decision

Deleting an owner-private Praxis conversation removes its row, message transcript and tool-call argument history from the active SQLite catalog within one transaction. A running agent request must finish or be canceled before the conversation can be deleted; the worker checks the run state, independent of the user-interface control, so no model/tool operation can commit into a removed conversation while processing a turn.

Conversation-linked saved memories, knowledge proposals and authored artifacts are **separate first-party records** and survive chat deletion with their `conversation_id` cleared. Deletion does not revoke captured files, delete authored artifact source originals, reverse applied knowledge changes or withdraw a payload already sent to a remote provider. Those records must be managed through their respective owner controls. The user-facing confirmation says so.

The delete action is owner-only in the first-party worker protocol, not exposed as a tool an agent can invoke. Unknown IDs return an unchanged result; deleting an active conversation is rejected explicitly rather than canceling work asynchronously and risking a late write. Rename changes only the conversation title and its update timestamp.

## Privacy and limitations

Deletion is **logical** deletion from the active catalog. It is not cryptographic erasure from SQLite pages/WAL or prior database backups, nor removal of provider-side chat artifacts. Durable removal of other saved knowledge requires separate authorized operations. The database uses the existing catalog writer transaction, with explicit message and tool-call cleanup rather than assuming foreign-key cascade is enabled.

A later product slice can add archive, searchable history and broader owner retention controls; it must not silently weaken existing disclosure or leave orphaned chat tool-call records.

## Verification

Test transactional deletion, missing/duplicate deletes, title normalization, active-run rejection, orphan-free transcript cleanup, preservation of independent saved records, native confirmation and restart behavior on owner-Mac before accepting.
