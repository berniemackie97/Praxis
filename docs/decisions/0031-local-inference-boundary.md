# ADR 0031: Local inference boundary and model profiles

**Status:** Accepted. Schema v11. Implements the first part of #31 for local models only. Project layout superseded by [ADR 0039](0039-layered-architecture.md): the model ports live in Praxis.Application.Inference and the adapters in Praxis.Infrastructure.Inference.

## Context

Enrichment (#115) will use a local model (currently Qwen3.5-9B Q4_K_M through Ollama on the owner's 16 GB Apple Silicon Mac, about 11 GiB usable by the GPU). The owner wants local models to be easy to swap, may also run llama.cpp or MLX servers, and will later add remote providers (DeepSeek, Kimi, Gemini, Anthropic, OpenAI) only through per-request approval. Model failures must never corrupt the catalog, and no configuration may silently send content off the machine.

## Decision

- **`Praxis.Inference` project** (BCL only, no catalog dependency) defines the boundary: `IChatModel` with `DescribeAsync` (exact model build: provider, name, digest, family, size, quantization, recorded with results for provenance and cache keys) and `CompleteAsync` for **structured** requests (`ChatRequest`: bounded messages, required JSON Schema, output and context token limits, temperature). Free-form generation is not part of the contract.
- **Failures are codes, not text.** `InferenceException.Failure` → `model_unavailable`, `model_timeout`, `model_missing`, `model_rejected`, `model_server_error`, `model_truncated`, `model_invalid_response`. Provider error bodies (which can echo prompts or paths) are never propagated.
- **Ollama adapter** uses the native `/api/chat` endpoint: non-streaming, `think: false`, `format` set to the schema, fixed seed, `num_ctx`/`num_predict` per request. Responses are capped at 1 MiB, requests have a deadline (3 minutes by default; caller cancellation stays distinct from timeout), proxies are disabled, and output stopped at the token limit is reported as truncated instead of being parsed.
- **Loopback only.** `LoopbackEndpoint` accepts only `http(s)` loopback origins without path, credentials, query or fragment. Remote providers need their own consent and redaction workflow and are not representable in this configuration.
- **Model profiles live in the catalog** (`model_profiles`, keyed by role; the first role is `local-enrichment`), so the choice travels with backups and is editable from the workbench through `modelProfile`, `modelProfileSave` and `localModels` (lists installed models at a loopback endpoint).
- `Praxis.Storage` references `Praxis.Inference`; the catalog's processing handlers are the consumers.

## Consequences and next steps

- An OpenAI-compatible adapter (llama.cpp, LM Studio, MLX servers) slots in behind `IChatModel` without changing callers. Praxis-managed runtime lifecycle (starting and stopping a server) is still open.
- The release dependency inventory now treats transitive project references as expected lockfile entries, matching NuGet's lock format.
- The model enrichment processor (3c) uses the `local-enrichment` profile and records model identity and token usage with its results.
