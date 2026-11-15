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

Simple replies and general questions are instructed to skip library tools. With **Use Praxis tools** off, the next request omits tool schemas and the CLI relay entirely. It keeps a bounded dialogue history, excludes prior tool traffic and stored preferences, and does not run source-grounding checks. This is a per-conversation composer choice for the current app session, not a provider or disclosure-policy change. CLI sessions advertise only tools allowed by the current permission tier and stop executing new Praxis calls after 12 calls (or a lower caller limit). This caps Praxis tool execution, not work inside the provider harness. Saved preferences have no per-source grant; Praxis no longer injects or recalls them for remote models. Previously saved transcript text is not scrubbed by this change.

The built-in loopback Ollama connection sends [`reasoning_effort: "none"`](https://docs.ollama.com/api/openai-compatibility#supported-request-fields) for both direct messages and tool turns. With the previous provider default, Qwen 3.5 generated excessive hidden reasoning for a one-word prompt and sometimes stopped after a lookup without a visible answer. Requesting no thinking fixed both fixture cases. Models that cannot disable thinking may still use their lowest effort. Other OpenAI-compatible connections do not receive this setting.

Ollama model discovery uses the local runtime's capability metadata rather than assuming every installed model is a chatbot. Chat only lists completion-capable models when metadata is available. Models without tool support, such as Gemma 2 and Gemma 3, never receive tool definitions. Managed local Ollama can still use the bounded library lookup described below. If metadata is unavailable, legacy name hints cover standard Gemma 2/3 and embedding tags. Tool-capable models still use Praxis's disclosure and permission checks.

Codex collects the reported input and output counts from `turn.completed` JSON events. The app shows counts when the provider supplies them. They include provider-side context and are not a subscription charge estimate; the reported usage-window drop cannot be inferred from them. See [Codex JSON event usage](https://developers.openai.com/blog/eval-skills).

Codex runs ephemerally with a read-only sandbox and requests that shell, image access, connectors, built-in web search and delegation be disabled. Noninteractive approval overrides apply only to the Praxis search, source open, data describe/query and calculator tools. The relay still enforces Praxis policy; shell and writes receive no approval override. A failed turn that requests interactive approval shows a bounded error rather than provider text. See the [Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference).

## Local library recall

With library context enabled, managed local Ollama runs indexed text search before generating a reply. It provides up to four short excerpts from current permitted sources as untrusted evidence, with citations. No separate generation or embedding call runs for this lookup. It is lexical recall: synonyms, implicit references and some short or multilingual queries may need a different query or a tool-capable model. Other providers receive no automatic excerpts. Ollama cloud aliases are rejected on the built-in local connection. These excerpts are not added to the transcript; the resulting answer is still conversation history subject to existing retention and disclosure behavior.

**Save as Topic Note** lets the owner edit a reply down to a reusable finding before capture. It does not import transcripts, automatically promote model guesses, or overwrite existing notes. Typed chat recaps, revisioned topic updates, and review for conflicts remain tracked under [#440](https://github.com/berniemackie97/Praxis/issues/440), [#442](https://github.com/berniemackie97/Praxis/issues/442), and [#445](https://github.com/berniemackie97/Praxis/issues/445). Captured Markdown is library evidence; its name does not grant it authority to change the app's instructions or policy.

## Verification

Test long transcripts with and without summaries, complete user/tool turn retention, preserved current request, omission counts and short histories. Run locked restore, Release build, full tests, format, macOS app/worker checks and private owner smoke with Codex and Claude Code. GitHub CI must complete green before merge.
