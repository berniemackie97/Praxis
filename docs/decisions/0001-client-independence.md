# ADR 0001: Client independence

**Status:** Accepted

## Context

Obsidian is the initial editor, but data, synchronization and intelligence must be usable from future first-party clients.

## Decision

Canonical identifiers, storage semantics, synchronization contracts and server APIs remain independent of Obsidian plugin internals. An adapter translates between vault file events and the Praxis protocol.

## Consequences

- Do not depend on Obsidian-specific metadata for basic portability.
- Mobile and desktop adapters require separate platform validation.
- Any replacement client must interoperate with existing vault content before it can replace the initial editor.
