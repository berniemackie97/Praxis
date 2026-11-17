# Play video clips in Praxis Chat

Put a supported MP4, MOV, or M4V recording into an approved intake folder. On macOS 26 or later, Praxis can use Apple's on-device speech recognizer when it supports the language. Praxis preserves the original and indexes what was said. It does not infer what's visible on screen from speech. A digest-bound caption sidecar can also supply timed evidence.

Ask “Find the clip where they tighten the spindle.” Praxis returns matching clips directly, even with tool calls off. Click **Open Clip** below the answer to load its inline player. Choose **Play Full Video** to continue past the clip endpoint. Reopening the chat preserves the citation and its source revision. Codec errors and invalid or stale citations show a message instead of loading a replacement silently.

Direct clip requests search the timed transcripts before consulting any model, even with chat tools off. This works with existing transcripts: the player maps on-device speech timings to the local video’s audio track when you open the clip. Ask for a fresh clip if a saved card predates the timing check. On complex edits or videos with multiple audio tracks, attach a verified caption sidecar for exact clip playback. Web clips connect to the provider only when opened; if a YouTube embed is blocked, the browser link preserves the start timestamp.

Verification used an isolated 18-second MP4 with delayed audio: the audio starts 2.954 seconds after the video. The player maps clip bounds to the video clock before seeking. Chat saves the exact source revision and time bounds, and rejects citations to a replaced source. This verifies the supported path, not every codec, language, edit list, or online provider.

## Learning from a saved web video

Save a supported video link in Chat. Published captions are used first. In **Privacy → Links**, enable media study to allow bounded direct-media downloads and local speech when captions are absent. Enable sampled frames separately for timestamped OCR, then optionally choose an installed local vision-capable model for descriptions. New media study is off by default. Activity can pause or resume saved-link processing.

The Brain records transcript origin, sampled frame hashes/timestamps, and any unverified model descriptions. Speech does not establish what happened visually; up to twelve sampled frames do not cover every moment. Missing media formats, unavailable speech models, decoding errors and consent changes produce partial or unavailable results. Audio/video downloads are temporary; the captured snapshot preserves timed speech and sampled visual witnesses. No cloud vision fallback or automatic downloader/model installation occurs. See [budgets and acceptance](delivery/living-brain.md).
