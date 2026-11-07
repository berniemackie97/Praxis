# Delivery roadmap

The GitHub [master roadmap](https://github.com/berniemackie97/Praxis/issues/1) and its epic/story/task issues track delivery. Each stage ends in a tested, usable outcome rather than a collection of unintegrated components.

| Stage | Epic | Outcome |
| --- | --- | --- |
| M0 | [Foundation](https://github.com/berniemackie97/Praxis/issues/2) | Reproducible service, CI, review process, and requirements evidence |
| M1 | [Security and canonical knowledge](https://github.com/berniemackie97/Praxis/issues/3) | Trust boundary, identity, stable records, revisions, and recovery contract |
| M2 | [Versioned synchronization](https://github.com/berniemackie97/Praxis/issues/4) | Authenticated incremental offline sync, conflicts, attachments, and replay |
| M3 | [Obsidian integration](https://github.com/berniemackie97/Praxis/issues/5) | Tested desktop/mobile adapter and representative vault recovery |
| M4 | [Capture and retrieval](https://github.com/berniemackie97/Praxis/issues/6) | Provenance-preserving capture and source-grounded, rebuildable search |
| M5 | [Authorized agents and integrations](https://github.com/berniemackie97/Praxis/issues/7) | Constrained tools, approvals, connectors, and durable workflows |
| M6 | [Independent user interfaces](https://github.com/berniemackie97/Praxis/issues/8) | Alternative web and native clients using the same canonical knowledge |
| M7 | [Operations and release quality](https://github.com/berniemackie97/Praxis/issues/9) | Verified recovery, monitoring, migration, hardening, and releases |

## Milestone gates

- **M0:** Clean build, test, formatting, and PR gates; implementation task and requirements work items exist.
- **M1:** Identity and record/attachment contracts documented and tested with authorization and round-trip data checks.
- **M2:** Repeatable multi-client tests show no silent loss under offline edits, conflict races, retries, renames, deletes, and failures.
- **M3:** Representative vault content survives real desktop/mobile synchronization and restore.
- **M4:** Relevant sources are captured idempotently, indexes rebuild, and relevance/citation quality is measured.
- **M5:** Agents cannot act without authority; all durable actions are observable, resumable, and safe under retries.
- **M6:** Data can be created, edited, searched, and synchronized without Obsidian.
- **M7:** Restore and upgrade operations are rehearsed, documented, and supported by tests.

## Engineering order

Threat modeling and canonical identity precede sync writes. A defined sync contract precedes Obsidian adapters. Stable records and permission rules precede ingestion and retrieval. Explicit authorization precedes agent writes or external actions. Operational safeguards develop alongside each feature, not only at the end.

## Unresolved choices

Host topology and cost ceiling, encryption and server plaintext permissions, device recovery requirements, concrete data-source priorities, and mobile background behavior must be resolved with evidence before expensive implementation.

An epic can overlap later work in documentation or testing, but implementation is gated by the contracts it depends on.
