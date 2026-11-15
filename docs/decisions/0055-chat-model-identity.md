# ADR 0055: Pin chat model and effort per conversation

**Status:** Proposed — [#433](https://github.com/berniemackie97/Praxis/issues/433), first model-choice slice.

## Context

A Praxis conversation used to retain only `backend_id` alongside its title. Chat backend profiles share their model and effort with the Chat role and other setup operations. Consequently, changing a provider's configured model silently changed the model invoked for every existing conversation, even when the owner did not choose that change. The first-party chat picker showed only a connection, with no model or reasoning-effort choice.

## Decision

Catalog v31 adds nullable `model` and `effort` fields to the existing `agent_conversations` table. During migration, existing conversations snapshot the current model and effort of their configured backend. If that backend no longer exists, the legacy row keeps `NULL` and still follows the backend's model only if the owner reconnects it. New conversations always persist an explicit model snapshot and an optional reasoning-effort snapshot.

The owner chooses a connection, a model advertised by its available catalog (or the currently configured model), and supported effort before starting a conversation. The selected model and effort are saved with the conversation and passed to the runtime's existing backend factory for *every* turn, including after an app restart. A subsequent provider-level model/effort change cannot silently substitute the conversation's pinned choices. The running conversation shows the selected identity rather than an editable connection picker; switching to a different model requires starting a new conversation in this slice.

The native picker uses the existing Models discovery and catalog; there is no new provider registry, and the selected backend ID remains the identity used for source disclosure policy. Reasoning-effort choices are presented for supported Codex and Claude Code models, not for local or HTTP backends without provider-confirmed effort capabilities. CLI processes continue using the existing safe argument-list invocation and private worker relay. Missing/unavailable models return a normal provider failure; Praxis never silently substitutes another model.

## Boundaries and follow-ups

The provider connection remains owner-configured. Changing its executable, endpoint, credentials or enabled state is governed by that provider configuration; this slice does not freeze provider executables or introduce new connection versioning. Never claim that model pinning alone authorizes remote disclosure. Explicit per-source rules continue to apply.

This slice does not add context-window selection, provider-reported usage tracking, streaming, or a model-change action within an existing conversation. Those are separately governed by #433 and require measured provider capabilities. Model identity and token counts may not be inferred from a CLI session that reports neither.

## Verification

Release build, v30-to-v31 migration and reopened-catalog checks, local/CLI backend factory assertions, native JSON decoding and picker smoke, full suite and macOS worker tests are required. The owner must observe the selected model/effort actually reaching a configured provider in a safe chat smoke test. GitHub-hosted CI must pass before merge.
