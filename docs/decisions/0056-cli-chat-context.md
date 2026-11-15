# ADR 0056: Bounded CLI chat history

**Status:** Proposed — [#449](https://github.com/berniemackie97/Praxis/issues/449), CLI context and Chat reliability slice.

## Context

The session-based Codex and Claude Code backends receive a rendered conversation in one CLI request. Before this slice, runtime forwarded every persisted message since the most recent summary; only turn-driven HTTP chat invoked the existing summarization path. Long-running CLI conversations could therefore send arbitrarily long historical prompts, even though the owner had no visibility into context omission or a truthful provider token estimate.

## Decision

Apply a **24,000-character CLI transcript ceiling**, or a lower explicit `AgentRunLimits.CompactAfterCharacters` limit. The current owner request is exempt from the history guard; its separate request-size limit still applies. The persisted transcript is not changed, summarized, deleted or reordered. The latest owner request is always preserved. Select the most recent **complete** earlier owner turns that fit, accounting for message text plus bounded framing overhead; never forward half of a previous user/tool/assistant interaction. Internal repair instructions remain attached to the turn they repaired. An already-persisted earlier summary can be included if it fits; otherwise it is omitted, never rewritten or reconstructed.

When history is omitted, the CLI agent also receives a system-level notice not to infer missing earlier decisions. The CLI renderer already omits raw tool-result messages. The omitted count covers only nonempty transcript messages that the renderer would otherwise forward. The count is returned with the reply, and native Chat identifies the excluded older messages and explains that the stored conversation remains intact. A progress event describes the bound without exposing message content. No additional catalog migration, provider permissions or remote routing are introduced.

## Boundaries

A character limit is **not a tokenizer** and does not represent a provider's context window or actual model token usage. The code does not infer tokens from characters or report an invented usage figure. It does not guarantee that a model accepts a request; the backend still owns provider failures and timeouts. Model-specific budget discovery, explicit owner-selected context limits, local HTTP/context compaction repair, provenance-rich handoffs and history redaction on policy revocation are follow-ups under #433; #431 still owns real-provider response smoke. The existing HTTP compaction remains unchanged by this slice.

## Usage and tool access

Simple replies and general questions are instructed to skip library tools. CLI sessions advertise only tools allowed by the current permission tier and stop executing new Praxis calls after 12 calls (or a lower caller limit). This caps Praxis tool execution, not work inside the provider harness. Saved preferences have no per-source grant; Praxis no longer injects or recalls them for remote models. Previously saved transcript text is not scrubbed by this change.

Codex collects the reported input and output counts from `turn.completed` JSON events. The app shows counts when the provider supplies them. They include provider-side context and are not a subscription charge estimate; the reported usage-window drop cannot be inferred from them. See [Codex JSON event usage](https://developers.openai.com/blog/eval-skills).

Codex runs ephemerally with a read-only sandbox and requests that shell, image access, connectors, built-in web search and delegation be disabled. Noninteractive approval overrides apply only to the Praxis search, source open, data describe/query and calculator tools. The relay still enforces Praxis policy; shell and writes receive no approval override. A failed turn that requests interactive approval shows a bounded error rather than provider text. See the [Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).

## Verification

Test long transcripts with and without summaries, complete user/tool turn retention, preserved current request, omission counts and short histories. Run locked restore, Release build, full tests, format, macOS app/worker checks and private owner smoke with Codex and Claude Code. GitHub CI must complete green before merge.
