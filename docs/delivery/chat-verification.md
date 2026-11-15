# Chat recovery verification on the owner's Mac

Track live provider acceptance in [#431](https://github.com/berniemackie97/Praxis/issues/431). Quit any other Praxis instance, then run from a clean checkout of the revision under review:

```bash
dotnet restore Praxis.slnx --locked-mode &&
dotnet build Praxis.slnx -c Release --no-restore &&
dotnet test Praxis.slnx -c Release --no-build --no-restore &&
dotnet format Praxis.slnx --no-restore --verify-no-changes &&
bash scripts/macos/build-desktop.sh --archive-existing &&
bash scripts/macos/test-worker-session.sh &&
bash scripts/macos/capture-screenshots.sh &&
open .artifacts/Praxis.app
```

The automated Chat test uses a disposable catalog and scripted local CLI. It covers a failing conversation and a successful one while another is running, changed model/effort on the same conversation, token reporting from fixture events, and isolated cancellation. The tests don't sign in or spend provider quota.

For live acceptance, use fresh chats and nonsensitive prompts. Don't upload transcripts, provider output, your catalog, or private screenshots to GitHub.

1. Turn off **Use Praxis tools** and send one Codex message at the lowest supported effort: `Reply with PING only.` Expect PING without source citations or library calls. Note the model, effort, elapsed time and reported input/output tokens, or record that no counts appeared. Don't treat these counts as a change in the plan's usage percentage. Skip further Codex messages if the cost of this single check is unacceptable.
2. Send the same one-turn prompt to local Ollama and Claude Code with tools off. Verify exact response, no unrelated citations, and persistence after restarting the app. Record elapsed time and input/output tokens. The built-in loopback Ollama connection requests thinking off; [models that cannot disable thinking](https://docs.ollama.com/api/openai-compatibility#supported-request-fields) may still reason at their lowest supported effort.
3. In the Ollama or Claude chat, change to another available model and (where supported) change effort. Keep the same conversation and connection. Send `Reply with PONG only.` Check the setting and both answers after restart. Changing the model must not change the provider or privacy policy.
4. Start a longer local response and switch conversations while it runs. Leave an unsent draft in each chat. Confirm both can run, the list shows the active entries, and only the selected chat stops. A third send should keep its draft and report that two chats are already responding. Also verify an unavailable local endpoint reports an error only in its own chat; don't change your working connection or disclosure settings just to force a failure.
5. Turn on **Use Praxis tools**. In a disposable library, index a note named `Workshop inventory.md` with the sentence “The inventory code for the spare bracket is ORCHID-PLUM.” Ask Ollama: `In my fixture library, what is the inventory code for the spare bracket? Cite the source.` Expect the exact code and a citation pointing to that note. For Codex, test source retrieval only if you choose to make another remote request and the fixture is already allowed by your policy. Verify the narrow read behaves without an interactive approval, the cited source opens when expanded, and disallowed evidence stays withheld. Leave a failure open if a provider cannot complete this check.

For the Ollama compatibility check, select an installed Gemma 2 or Gemma 3 in Chat and send a short prompt. The composer should show **Plain chat · library tools unavailable**. The worker should deliver and save the reply without any tool traffic, even if a client requests tools. Embedding-only models (including renamed ones with capability metadata) must not appear in the Chat picker. To query library sources, use a tool-capable model.

Report the commit, provider version, outcome, sanitized failure category, and measured values only. A green scripted run does not satisfy live model acceptance. For [#433](https://github.com/berniemackie97/Praxis/issues/433), model-aware token limits, grounded local answer quality beyond the fixtures, and replay after disclosure revocation still need separate evidence.
