# ADR 0015: Safe first-run local Obsidian installation

**Status:** Proposed under #22 / #25 / #57.

## Decision

Offer a **manual, explicit installer** for the first local Obsidian desktop trial. It is a repository-local Node standard-library command, not a background service or generic plugin manager. It requires absolute paths to an existing Obsidian test vault, an external plaintext archive directory, a previously published `Praxis.VaultCli.dll` and the `dotnet` executable. It never discovers or installs itself into arbitrary production vaults.

Before writing anything under the vault, the installer runs the real CLI against a throwaway fixture in the operating system's temporary directory: capture a Markdown note and binary attachment, verify its tracked reference, audit its embed, and restore original bytes to a separate directory. Failure at any step refuses installation. It packages the local plugin using ADR 0014, then creates a new `praxis-vault` plugin directory and writes `main.js`, `manifest.json` and local path settings `data.json`.

Existing plugin installations are never replaced. Destination names are constrained and config/plugin directory symlinks are rejected. An installation write failure removes only the directory newly created by this installer; user notes and already installed plugins remain untouched. The temporary sample archive is not promoted to the user's archive directory.

## Desktop runtime boundary

A GUI-launched macOS Obsidian instance may not receive the Terminal's `PATH`. The plugin therefore supports an explicit absolute `dotnet` executable path. The archive session resolves and validates that path and passes it to the child-process runner. Previously configured installations without the field retain their original `dotnet` PATH fallback, while the new installer always configures the explicit binary to eliminate that dependency.

## Verification and outstanding acceptance

Cross-platform simulated installer tests cover path separation, original note preservation, refusal to overwrite, corrupted preflight, custom Obsidian configuration directory and plugin-directory symlinks. Linux CI also runs the installer against the **real** pinned .NET CLI, verifies successful installation and confirms the original test vault content remains unchanged. These tests complement, rather than replace, archive-session and CLI integration tests.

The manual [local trial guide](../../integrations/obsidian/LOCAL-TRIAL.md) describes capturing, previewing, updating, verifying and restoring a disposable vault. **No actual installed Obsidian application, representative existing vault or user device has yet passed this procedure.** Desktop integration #22, restore acceptance #25 and feature #57 remain open; mobile #23 and privacy decisions #50 are unaffected. Local archive functionality is not equivalent to automatic enrichment, AI-assisted organization or cross-device synchronization.
