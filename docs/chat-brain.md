# Brain context in Chat

Every Praxis Chat turn checks the indexed Brain locally before calling its model. You do not need to ask Codex, Claude Code, or a local model to use your notes. **Allow tools** controls model tool calls; automatic Brain recall is separate.

Praxis checks the current source revision and the provider's **Chat** disclosure permission, then includes only a small selection of excerpts. Codex and Claude Code receive no more than three excerpts, each at most 300 characters. Matching several terms can qualify when the full query has no match. A weak match is left out. A model with tools can search using a distinctive name or phrase, then open a specific passage when the initial brief doesn't answer the question. Raising reasoning effort changes the model's reasoning budget, not which sources it may receive.

Open **Brain Context** in the Chat toolbar to see the provider, selection outcome, candidate and withheld counts, and source revisions selected for recent turns. Selection is evidence of what Praxis prepared for the provider, not proof the answer relied on it or that a failed provider received it. Numbered citations in the reply remain the evidence for individual claims. Recall receipts retain source identifiers and revisions, not excerpt bodies. A new turn rechecks current grants and excludes newly tracked generated turns grounded in a source that is no longer allowed. Older chats predate per-message source tracking.

Both Codex and Claude Code use the owner's signed-in CLI and the Praxis relay. Praxis doesn't import the CLIs' own project tools or shell permissions. The Brain and the app's permissions remain the same regardless of which model is selected.

## Chat action approvals

With proposals allowed, Chat can request an exact macOS terminal command or HTTPS address. In **Approvals**, review the target and approve or reject it. Approval claims the request once; pending requests expire after 30 minutes. Terminal commands have a short deadline, run in an empty temporary workspace, and cannot read private files or use the network. Browser approval opens the address in the default browser; it doesn't read pages or click controls. Action output stays in the local catalog; remote providers receive status only. Opening an address does not grant the model access to the browser session.

This slice does not add installer presets, package installs, or host-wide tool permissions.
