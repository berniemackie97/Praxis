# ADR 0051: Activity view and recoverable intake

**Status:** Accepted (lead-architect decision with owner, November 14, 2026). Schema v29. Follows [ADR 0050](0050-machine-aware-local-models.md).

## Context

A live audit after ADR 0050 found the queue draining while Knowledge stayed the same:

- Every Claude Code call failed with an unusable answer. Praxis blocked all tools with `--disallowedTools "*"`, and that also blocked Claude Code's own `StructuredOutput` tool, which is how it returns schema answers. The owner's Claude Code settings, hooks and output style also leaked into Praxis's calls.
- Every Codex call failed: Codex runs structured outputs in strict mode, which rejects a schema unless every object forbids extra properties and lists all properties as required.
- When the owner lowered the daily limits below the calls already made that day, every remaining file was marked "waiting for a model" within seconds. Those files were retried only after another settings change, so a new day did not start them again.
- Local drafts never ran: before any measurement, `auto` only tried spans of at most 2,500 characters, and most spans are longer.
- Chat could not find a file by name. Its local model passed a wrong `kind` (`code` for a `.json` file), and a long question needed every word to match.
- The owner could not see what any model was doing, so a stuck queue and a busy queue looked the same.

## Decision

1. **Plan CLIs run clean.** Claude Code runs with no built-in tools, only `StructuredOutput` allowed, no setting sources and no slash commands (chat sessions also skip setting sources). Codex receives the response schema in strict form: closed objects, every property required, and optional properties nullable.
2. **Recoverable waits.** Files that waited because a daily limit or plan quota was reached are read again after midnight UTC, as well as after a settings change. Migration v29 queues the files that failed only because of the CLI bugs again.
3. **Drafts learn their pace.** Before any measurement, one span of any draftable size probes the local model at a time; other workers wait for its result.
4. **Search finds files by name.** A term in the file's name or path counts eight times a term in its text. When no source contains every word, chat search returns partial matches (mode `partial`). A `kind` filter that would hide every match is ignored, and the result says so.
5. **Activity.** A new Activity page (also opened from the sidebar status) shows model calls in flight with their file and elapsed time, running jobs, each stage's queue, each provider's calls today against its limit with recent failures, where every file ended up, recent calls with outcome and duration, and plain notes on anything blocking progress. Model calls record their duration (schema v29). In-flight calls are tracked in memory only.

## Consequences

- With both plans connected, Reading and Checking work again; Knowledge fills as files are read.
- A reached limit pauses intake until the next day instead of leaving files unread for good.
- The owner can tell at a glance whether Praxis is working, waiting or failing, and why.
