# ADR 0056: Bounded CLI chat history

**Status:** Proposed — [#449](https://github.com/berniemackie97/Praxis/issues/449), first Chat context-control slice.

## Context

The session-based Codex and Claude Code backends receive a rendered conversation in one CLI request. Before this slice, runtime forwarded every persisted message since the most recent summary; only turn-driven HTTP chat invoked the existing summarization path. Long-running CLI conversations could therefore send arbitrarily long historical prompts, even though the owner had no visibility into context omission or a truthful provider token estimate.

## Decision

Use the existing `AgentRunLimits.CompactAfterCharacters` as a conservative **character-based CLI history guard**. The persisted transcript is not changed, summarized, deleted or reordered. The latest owner request is always preserved. Select the most recent **complete** earlier owner turns that fit, accounting for message text plus bounded framing overhead; never forward half of a previous user/tool/assistant interaction. Internal repair instructions remain attached to the turn they repaired. An already-persisted earlier summary can be included if it fits; otherwise it is omitted, never rewritten or reconstructed.

When history is omitted, the CLI agent also receives a system-level notice not to infer missing earlier decisions. The CLI renderer already omits raw tool-result messages. The omitted count covers only nonempty transcript messages that the renderer would otherwise forward. The count is returned with the reply, and native Chat identifies the excluded older messages and explains that the stored conversation remains intact. A progress event describes the bound without exposing message content. No additional catalog migration, provider permissions or remote routing are introduced.

## Boundaries

A character limit is **not a tokenizer** and does not represent a provider's context window or actual model token usage. The code does not infer tokens from characters or report an invented usage figure. It does not guarantee that a model accepts a request; the backend still owns provider failures and timeouts. Model-specific budget discovery, explicit owner-selected context limits, local HTTP/context compaction repair, provenance-rich handoffs and history redaction on policy revocation are follow-ups under #433; #431 still owns real-provider response smoke. The existing HTTP compaction remains unchanged by this slice.

## Verification

Test long transcripts with and without summaries, complete user/tool turn retention, preserved current request, omission counts and short histories. Run locked restore, Release build, full tests, format, macOS app/worker checks and private owner smoke with Codex and Claude Code. GitHub CI must complete green before merge.
