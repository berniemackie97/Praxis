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

1. Turn off **Use library and tools** and send one Codex message at the lowest supported effort: `Reply with PING only.` Expect PING without source citations or library calls. Note the model, effort, elapsed time and reported input/output tokens, or record that no counts appeared. Don't treat these counts as a change in the plan's usage percentage. Skip further Codex messages if the cost of this single check is unacceptable.
2. Send the same one-turn prompt to local Ollama and Claude Code with tools off. Verify exact response, no unrelated citations, and persistence after restarting the app. Record elapsed time and input/output tokens. The built-in loopback Ollama connection requests thinking off; [models that cannot disable thinking](https://docs.ollama.com/api/openai-compatibility#supported-request-fields) may still reason at their lowest supported effort.
3. In the Ollama or Claude chat, change to another available model and (where supported) change effort. Keep the same conversation and connection. Send `Reply with PONG only.` Check the setting and both answers after restart. Changing the model must not change the provider or privacy policy.
4. Start a longer local response and switch conversations while it runs. Leave an unsent draft in each chat. Confirm both can run, the list shows the active entries, and only the selected chat stops. A third send should keep its draft and report that two chats are already responding. Also verify an unavailable local endpoint reports an error only in its own chat; don't change your working connection or disclosure settings just to force a failure.
5. Turn on **Use library and tools**. In a disposable library, index a note named `Workshop inventory.md` with the sentence “The inventory code for the spare bracket is ORCHID-PLUM.” Ask Ollama: `In my fixture library, what is the inventory code for the spare bracket? Cite the source.` Expect the exact code and a citation pointing to that note. For Codex, test source retrieval only if you choose to make another remote request and the fixture is already allowed by your policy. Verify the narrow read behaves without an interactive approval, the cited source opens when expanded, and disallowed evidence stays withheld. Leave a failure open if a provider cannot complete this check.

6. Select an installed Gemma 2 or Gemma 3 in Chat and repeat the inventory question with **Use local library context** on. The reply should give the code and cite the note, with no tool calls. Turn the setting off and ask from a fresh chat to check that no library context is added. Embedding models must stay out of the Chat picker. Custom and remote connections do not receive this automatic lookup.
7. On a fresh reply, open **Save as Topic Note…**, give the note a title such as “Workshop inventory decision,” and edit the body to keep only the confirmed detail. Check the Inbox and its eventual indexing. The original conversation stays in Chat. Capture follows the active intake policy; use synthetic data or a local-only policy for the smoke test.

## Verification queue

In a disposable library with pending verification, open **Verification** and select a request. Review the exact payload, select **Queue on This Mac**, and check that it advances to queued and then checked, or shows an actionable provider error. With multiple pending requests, select **Approve Pending Locally** and confirm the count. Only the displayed snapshot (up to 20) is eligible, and only on local Ollama. Requests that moved or no longer fit the budget stay unapproved. External providers still require individual payload review, explicit consent and each included source's permission. Editing the payload or changing the destination clears that consent.

Verification receives the reviewed quotes and short surrounding excerpts, with several observations for entity type conflicts. It can return uncertain when the evidence is incomplete. It does not see the whole library. The worker caps the local context and output before queueing and checks the cap again before sending. Triage is a separate local structured classification from file metadata and the first 2,500 characters where format hints don't suffice. It needs no chat tools; cross-document connections belong to later stages.

On the owner Mac, a disposable fixture with installed Gemma 3 classified three Markdown examples (inventory, financial, personal) correctly. A fresh inventory chat answered from the indexed note with the correct code and source, no tools, and Ollama reported 281 input and 30 output tokens. This is a single recall check and three triage examples, not a relevance benchmark or a subscription usage estimate.

Report the commit, provider version, outcome, sanitized failure category, and measured values only. A green scripted run does not satisfy live model acceptance. For [#433](https://github.com/berniemackie97/Praxis/issues/433), model-aware token limits, grounded local answer quality beyond the fixtures, and replay after disclosure revocation still need separate evidence.
