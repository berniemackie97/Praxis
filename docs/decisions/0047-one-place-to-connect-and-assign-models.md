# ADR 0047: One place to connect models and assign what each does

**Status:** Accepted (lead-architect decision with owner, November 13, 2026). Schema v26. Refines the configuration surface of [ADR 0043](0043-tiered-intake-disclosure-and-agent-runtime.md); disclosure policy is unchanged.

## Context

Models were configured in two unrelated places: "Models" (intake and verification providers, with cost weights, a 1–500 daily-limit stepper and a hand-typed CLI path that rendered invisible when empty) and "Agents" (chat backends, configured separately, plus tier pickers that could only choose a provider). Claude Code or Codex had to be set up twice, the worker could not find CLIs because the app starts it with a minimal PATH, no role could choose its own model or reasoning effort, and "unlimited" did not exist.

## Decision

1. **Connections.** Praxis finds Claude Code, Codex and Ollama where installers put them (PATH, Homebrew, native installers, npm, bun, Volta, nvm), reports version and sign-in (`claude auth status`, `codex login status`) and reads Codex's model catalog with per-model reasoning efforts (`codex debug models --bundled`). Connecting a tool once creates both its intake/verification provider and its chat backend. Sign-in runs the tool's own login in Terminal; Praxis never handles those credentials. A different copy can be chosen with a file picker.
2. **Roles.** Chat, Triage (local tier), Reading (strong) and Checking (strongest) each choose a connection, a model and an effort. Tiers store their own model and effort, so one plan can serve two tiers (for example Sonnet for Reading and Opus for Checking). "Use Recommended" fills roles from what is connected and signed in, preferring a different vendor for Checking.
3. **Effort.** Claude Code receives `--effort`, Codex `-c model_reasoning_effort=…`; "Plan default" omits the model flag.
4. **Daily limits.** `0` means unlimited and is the default for new connections; presets and any custom number up to 100,000 are available.
5. Cost weights stay internal defaults. Recent model calls move to a collapsed activity list on the same page. Settings saves each change immediately.

## Consequences

- One tab, no duplicate setup, no typed paths in the common case.
- Sub-agent work at chat time uses the Triage model; the CLIs' own sub-agents remain off because Praxis runs them without their built-in tools.
