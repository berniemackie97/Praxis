# ADR 0046: Hands-off intake and confirmed knowledge

**Status:** Accepted (lead-architect decision with owner, November 13, 2026). Schema v25. Builds on [ADR 0044](0044-generic-intake-engine.md) and [ADR 0045](0045-media-moments-links-projects-and-records.md).

## Context

Intake is meant to be the most hands-off part of Praxis. In practice the Library asked the owner to accept or reject every Markdown heading and every local-model guess, and the Knowledge list showed all of them as if they were facts. With a small local model and a few misfiled folders (for example a repository's `.git` internals), Knowledge filled with names like `refs/heads/main` and the owner had to clean it by hand.

## Decision

1. **Knowledge is confirmed by default.** An entity mention counts as knowledge when the owner accepted or renamed it, a verifier supported it, or the strong or strongest tier produced it and code verified its quote (ADR 0044). One SQL view, `confirmed_entity_mentions`, defines this; Knowledge, the Library and the agent's knowledge tools read it.
2. **Local-model guesses are suggestions.** They stay candidates (`visible_entity_mentions`, which verification plans over) but are not listed as knowledge unless the owner asks to see unconfirmed names, settles them (one by one or all at once), or a stronger model checks them.
3. **Headings are structure, not knowledge.** The Markdown heading stage is retired; headings remain in the text, search and segments. Existing heading suggestions are no longer listed.
4. **The Library shows what was found**, grouped (people, organizations, projects and things, topics, relations), with rename and remove on each item. Unconfirmed guesses sit in one collapsed line with Add All, Dismiss All and Check with a Stronger Model.

## Consequences

- With a Reading (strong) model configured, files become knowledge with no owner action.
- Local-only setups keep everything private; their guesses wait as suggestions instead of polluting Knowledge.
- Owner decisions always win; removing an item rejects it everywhere.
