# Changelog

## Unreleased

Fixes from the first test in a real Unity Editor and real players (Unity 6.6: Play mode, a macOS Mono player, a WebGL IL2CPP build):

- WebGL: no `app_unclean_exit` after a closed or reloaded tab. A tab has no quit event, so every reload was reported as a crash.
- Errors of the Unity Editor itself (for example the search index) are no longer sent from Play mode.
- Release players: the module ID in a stack frame (`(at <b67c...>:0)`) is no longer used as the file. It changes with each build, so the same error got a new fingerprint in each build.
- The `Basic` sample has a scene (`Basic.unity`) with a demo panel.
- Refused events of an accepted batch (`202` with `rejected` above 0, P-2787) are dropped, logged with their reason codes, and reported by `SendFailed(202, count)`. `Flushed` counts only the stored events.

## 0.1.0 (2026-11-24)

First version. Same behavior and wire format as the Godot SDK 0.1.0:

- `Init`, `Track`, `Identify`, `Reset`, `SetConsent`, `Flush`, `Shutdown`, `LinkUrl`, `SetEndpoint`.
- Persistent anonymous ID, sessions that rotate after 30 minutes without focus or events and when they are 24 hours old (a game that stays open gets a `session_end` and a `session_start` every 24 hours), automatic `game_open`, `session_start` and `session_end`.
- A refused batch is dropped and never sent again. The warning in the log has the reason of the collector, for example `SESSION_ID_REQUIRED`. `ITransport.Send` gives the response body to its callback for this.
- Offline queue on disk (PlayerPrefs on WebGL), batches of up to 50, retries with backoff and jitter, a 7-day limit.
- `TrackError` and automatic capture of exceptions, `Debug.LogError` and failed asserts. `app_unclean_exit` after a crash.
- `Enabled` option (keep editor runs out of reports), `Flushed` and `SendFailed` events.
- Refuses secret keys and API keys.
- Consent fails closed: an opt-out marker file, atomic writes, and an unreadable or damaged state keeps the SDK off.
