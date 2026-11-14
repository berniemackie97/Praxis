# ADR 0050: Machine-aware local models

**Status:** Accepted (lead-architect decision with owner, November 14, 2026). Schema v28. Tunes [ADR 0049](0049-hand-off-cascade.md) for smaller machines; options for larger machines are unchanged.

## Context

An audit on the owner's M4 with 16 GB found intake effectively stalled:

- Three different local models were in use at once (enrichment, triage, chat). Praxis also sent a different context size for triage (4k), drafts (16k), enrichment (4k) and chat (8k). Ollama restarts a model whenever the model or its context size changes, and with models on an external disk and swap nearly full, each reload took 80–150 seconds.
- Measured generation speed: gemma3 4B 21.7 tok/s, Gemma-4 E4B IQ4_XS 19.8, E4B Q4_K_M 13.4, gemma2 9B 7.7. A local draft at 9B costs minutes, more than the cloud review saves.
- Files left index-only before models were connected were never re-read: the pipeline only checked for reopenable work when nothing was running, and never asked a stage again after it once said no. The always-busy background enrichment starved it.

## Decision

1. **One resident local model.** One choice ("Local model") serves triage, local drafts, private chat and the optional background suggestions. Every local call uses the same context size (8k, larger steps only when a request needs more) and keeps the model loaded for 30 minutes.
2. **Drafts by measurement.** Local drafts are `auto` by default: Praxis learns the local model's pace from its own calls and asks for a draft only when it is expected to finish within a budget (20 s on 16 GB). Before any measurement it probes with a short document. `always` and `off` remain available.
3. **Optimize for this Mac.** From physical memory and the installed models, Praxis recommends and applies a plan:
   - Up to 18 GB: the largest model of at most 4 GB, the smallest embedding model, drafts auto (20 s), background suggestions off.
   - Up to 36 GB: models up to 7 GB, drafts auto (40 s).
   - Larger machines: models up to 16 GB, drafts always, suggestions on.
4. **Reopening is checked continuously.** Each stage is asked again at most once a minute while other work runs, and immediately when the model setup changes.
5. Optional enrichment that is paused no longer counts in the status line. Triage moves into the local model setting; the role list keeps Chat, Reading and Checking.

## Consequences

- On 16 GB, cloud models do the substantial reading, the local model handles fast triage and small drafts without reloads, and nothing waits on a stalled queue.
- Machines with more memory get more local work automatically, and every option can still be set by hand.
