# Brain context in Chat

Every Praxis Chat turn checks the indexed Brain locally before calling its model. You do not need to ask Codex, Claude Code, or a local model to use your notes. **Allow tools** controls model tool calls; automatic Brain recall is separate.

Praxis checks the current source revision and the provider's **Chat** disclosure permission, then includes only a small selection of excerpts. Open **Chat → Context** beside Model and Effort to pick Compact, Balanced, or Expanded for this conversation. These presets keep up to approximately 8,000, 24,000, or 48,000 characters of prior dialogue and offer up to one, three, or four short excerpts to a remote model (slightly more for local models). Prior dialogue is omitted in whole turns when it exceeds the budget. The current ask and active tool traffic stay visible; the provider's actual token window and disclosure policy do not change. Matching several terms can qualify when the full query has no match. A weak match is left out. A model with tools can search using a distinctive name or phrase, then open a specific passage when the initial brief doesn't answer the question. Raising reasoning effort changes the model's reasoning budget, not which sources it may receive.

Open **Brain Context** in the Chat toolbar to see the provider, selection outcome, candidate and withheld counts, and source revisions selected for recent turns. Selection is evidence of what Praxis prepared for the provider, not proof the answer relied on it or that a failed provider received it. Numbered citations in the reply remain the evidence for individual claims. Recall receipts retain source identifiers and revisions, not excerpt bodies. A new turn rechecks current grants and excludes newly tracked generated turns grounded in a source that is no longer allowed. Older chats predate per-message source tracking.

Both Codex and Claude Code use the owner's signed-in CLI and the Praxis relay. Praxis doesn't import the CLIs' own project tools or shell permissions. The Brain and the app's permissions remain the same regardless of which model is selected.

## Chat action approvals

With proposals allowed, Chat can request an exact macOS terminal command or HTTPS address. In **Approvals**, review the target and approve or reject it. Approval claims the request once; pending requests expire after 30 minutes. Terminal commands have a short deadline, run in an empty temporary workspace, and cannot read private files or use the network. Browser approval opens the address in the default browser; it doesn't read pages or click controls. Action output stays in the local catalog; remote providers receive status only. Opening an address does not grant the model access to the browser session.

Before proposing software installation, Chat can check standard command locations without starting a program. Supported macOS actions are one Homebrew core formula or one exact npm package version via the installed package manager. The owner sees and approves the target; a network install runs in the background against the existing package-manager prefix. npm lifecycle scripts are disabled. There are no REA or Caveman presets, private duplicate environments, or automatic package installs.

In a local Ollama chat, a direct correction such as `"Old label" is now called Current Name. Save that to memory.` records the current name and uses the older label to match existing sources. With **Propose edits**, the exact pair appears in **Approvals → Things to remember**. With **Allow edits**, it applies immediately. The correction doesn't change Privacy settings or rewrite source documents.

To change Privacy from chat, ask for a specific setting, such as “Set Privacy to local only.” Praxis shows the requested change under **Approvals → Permission changes requested in chat**. **Confirm** applies that exact change; **Reject** leaves the existing setting in place. Chat text cannot confirm a pending change.


## Dream and living memory

Use `/dream` to inspect up to three authorized suggestions from a bounded page of the existing reconsolidation queue, or `/dream <instruction>` to investigate a particular topic. Dream can read evidence and stage knowledge or local skill proposals; its runtime cannot directly edit records, delegate, install tools, or change settings. Review proposed knowledge changes in Approvals and skill drafts in Skills. An empty eligible queue skips inference. The selected Chat model handles the operation; Activity → Brain work can prepare a new Dream draft with a connected local, Codex or Claude backend. You can change Model and Effort before sending.

Activity also exposes the existing independent Triage, Reading and Checking assignments. Changes govern future model calls, not work already in flight. Assigning a remote model does not grant source disclosure or replay completed intake.

Local chats can retrieve up to three relevant generated recaps from prior conversations. Recaps are navigation aids, not verified facts; they remain local even when remote source disclosure is allowed. Deleting a chat removes its indexed recap. Current-revision and revocation checks suppress recaps whose cited evidence is no longer eligible.

With a local embedding profile enabled, the worker incrementally updates semantic vectors during available periods. It skips new batches during active chats or tracked model calls; a running batch can take up to its own deadline to drain. Keyword recall remains available. See [living Brain acceptance and limits](delivery/living-brain.md).


Activity → **Learning from experience** controls local retrospective reviews. The default is After useful work: an eligible completed task can receive a short review, within four daily attempts and a 24,000-token reservation budget. Idle maintenance waits at least five minutes after completion; Manual review runs on request; Off stops new reviews. A configured, enabled local model and proposal permission are required. Useful deterministic results or cited source observations can support a draft procedure; neither a clean answer nor a source statement proves general success. Short matching installed skills can be proposed for revision against their current version. Every draft still needs owner approval in Skills.

**Dream during idle time** is separately off by default. Enable it to review changed knowledge locally at most once per configured interval (two hours by default). Interactive Chat interrupts and queues an active Dream for bounded resumption. Worker restart can resume proposal-only Dream work up to three total attempts; ordinary requests are never automatically replayed. Completed run receipts and learning review usage persist with the conversation. Forgetting a conversation removes its receipts and invalidates dependent learned procedures.

Activity → **Processor controls** pauses new claims, lets active leases drain, and persists pause intent across restart. Resume re-enables new claims. While Dream runs, model-heavy intake and link study yield new claims; capture and deterministic reading remain available. Existing calls drain under their own deadlines. These controls do not revoke source access or cancel already approved work.


## Commands and owner corrections

Type `/` or open the command menu in the composer. Select a command to show its chip and argument field; Return selects a suggestion before it can send work. Up/Down select suggestions and Escape dismisses the list. Removing the chip keeps its argument text. The worker supplies command names, descriptions and required permission tiers; composer hints do not grant access. Privacy changes refresh when the chat regains window focus, and the runtime independently checks permission again.

Dream now requires proposal permission and Brain tools before it calls a completion model. Missing permission produces a blocked receipt with the Privacy action. Codex receives the permitted `knowledge_propose` tool through its relay; direct writes remain excluded. Dream uses dedicated instructions, the current request and compact source/tool windows rather than earlier unrelated conversation context. It remains a bounded investigation, not an unbounded scan of the whole Brain.

During work, Chat identifies the command beside live progress. The saved result card reports application-observed candidates, source-backed entity reviews, actual review-item IDs and applied-change count. Generated prose cannot manufacture these counts. Dream applies zero corrections automatically. Receipts persist with the run and attach to canonical message sequences when the conversation reopens, including after older history falls outside the display window. Older runs without structured receipts are not retroactively assigned effects.

Unchanged entities reviewed against an opened source are skipped for 24 hours. Their fingerprints and originating run form an indexed cursor; changed evidence or owner decisions make a new fingerprint eligible. A no-op, failed or incomplete investigation cannot claim a successful review just because its prose sounds confident. A focused `/dream <instruction>` can revisit a topic explicitly.

For an owner correction, say what is wrong and ask Praxis to update its Brain, or use `/correct <statement>`. Praxis first retains the exact owner statement (up to 4,000 characters), labeled as owner-provided rather than independently verified. With Propose edits, it waits in **Approvals → Things to remember**. With Allow edits, it becomes active. Full text is available in review; the short title is not the stored statement.

A bounded mapping pass can use a local model without tool-call support. It can suggest supported type or relationship changes only for existing, disclosable entities explicitly named in the statement. Exact quote, target, vocabulary and freshness checks precede effects. Propose edits queues graph repairs; Allow edits can apply them. Distinct products/projects stay distinct: a relationship does not authorize merging their identities or inventing containment. Unknown or ambiguous targets leave the statement saved, explain that the graph remains unmapped, and request the missing labels or relationship. Folder-based regions can remain separate while their entities gain a canonical relationship; Chat does not claim a region layout rewrite it did not perform.

Active owner corrections are retrieved locally in a bounded relevant packet. They are not automatically disclosed to other remote conversations. Rejecting or forgetting a correction blocks a pending dependent repair. Separately saved standing memory follows the existing retention policy and can outlive the originating chat.

Provider totals remain reported usage, not a bill. Where Codex supplies a cache count, Chat now shows the cached subset separately; those tokens are already included in reported input. Older receipts cannot recover an unrecorded cache split. Model choice does not bypass permission or tool-catalog restrictions.
