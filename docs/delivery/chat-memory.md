# Private chat memory

Praxis queues a short local recap when it saves a successful reply. It batches nearby messages and can resume after restart. Open **Chat recap** above the transcript. Recaps stay with their chat and are deleted with it. No remote model receives them automatically.

With **Allow Edits** enabled, clear owner statements such as “We decided Orchid will list new coats on Fridays,” “Problem with Orchid shipping: …,” or “We fixed the Orchid badge” can update the one matching existing Markdown note in the approved Primary Inbox. Praxis preserves the rest of the note and its original revision. It skips missing or ambiguous destinations and matches repeated wording after normalizing punctuation and common confirmation phrases. Paraphrases can still need manual consolidation.

Tell a tool-capable local model “Update Orchid: replace ‘Ship on Fridays’ with ‘Ship on Mondays’.” The memory skill looks up the exact passage and version before replacing it. It can also correct or forget an existing standing memory. Changed files are classified local-only before the edit. This works on captured Inbox Markdown, not arbitrary files, externally mounted paths, instructions, or permissions.

With local Ollama selected, say “Choose your own name” or type `/name Lumen`. The saved display name is used in fresh local chats. This also works when the selected model does not support tools. The app's product name stays Praxis.

Smoke test in a disposable library with an installed local Ollama model: index `Orchid Trading Company LLC.md`, enable **Allow Edits**, send the example decision, and wait for the recap and an update to the same note. Repeat with “We agreed that Orchid will list new coats on Fridays” and check that the note still contains one entry. Change the assistant name and ask for it in a fresh local chat. Delete the original chat and verify its recap disappears while the separate topic note remains.

Follow-ups: broader topic matching and semantic deduplication (#440, #442, #445). This slice does not backfill older chats or automatically revise conflicting claims.
