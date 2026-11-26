# Changelog

## 0.1.3 (2026-11-26)

- Setup without code (G-2532): **Project Settings > Manalens** (also **Tools > Manalens > Settings**) creates `Assets/Resources/ManalensSettings.asset`. With a key and AutoInitialize on, the SDK starts by itself after the first scene loads. `Manalens.Init(options)` called in `Awake` wins. `Manalens.Init()` with no options uses the asset.
- The settings page checks the key and has **Send test event** (one `manalens_test_event` with build `editor`, straight to the collector, no install and no session) with the result and the collector's reason. In Play mode it shows tracking on or off, the anonymous and session IDs, the queue, the last send result and the recent events, with **Flush now**. The page is in an editor-only assembly (`Manalens.Editor`), not in player builds.
- New read-only properties: `Manalens.IsTracking`, `Manalens.LastSendResult` and `Manalens.RecentEvents` (the last 20 queued events as JSON; cleared by `SetConsent(false)`).

## 0.1.2 (2026-11-26)

- Fixed: a focus event after `Shutdown()` started a new, empty session. Now a focus event keeps only an open session alive; the next event starts a new one. Unity sends `OnApplicationFocus` on the first frames, so a game that calls `Shutdown()` early (for example the `with-unity` demo) sent one extra `session_start`. Same rule as the flush timer in 0.1.1.

## 0.1.1 (2026-11-26)

- Fixed: a quit added an empty session. While `Shutdown()` waited for the last send, the flush timer could fire and start a new session after `session_end`. So each quit counted one extra session with no events. Found in the `with-unity` example run (P-2676).
- Fixed: an install could be lost. `game_open` (with `is_first_open`) and `app_unclean_exit` stayed only in memory until the next save of the queue (a full batch, the flush timer, focus loss, or quit). A first launch with no network that was killed before then lost the install, and the next launch sent `is_first_open` false. Now the queue is written to disk as soon as these events are queued, before any send (P-2773).

Fixes from the first test in a real Unity Editor and real players (Unity 6.6: Play mode, a macOS Mono player, a WebGL IL2CPP build):

- WebGL: no `app_unclean_exit` after a closed or reloaded tab. A tab has no quit event, so every reload was reported as a crash.
- Errors of the Unity Editor itself (for example the search index) are no longer sent from Play mode.
- Release players: the module ID in a stack frame (`(at <b67c...>:0)`) is no longer used as the file. It changes with each build, so the same error got a new fingerprint in each build.
- The `Basic` sample has a scene (`Basic.unity`) with a demo panel.
- Refused events of an accepted batch (`202` with `rejected` above 0, P-2787) are dropped, logged with their reason codes, and reported by `SendFailed(202, count)`. `Flushed` counts only the stored events.
- WebGL: the state and the queue are in the browser's `localStorage`, written at once (a `.jslib` plugin). With PlayerPrefs, a reload right after the first launch lost the state and made a new anonymous ID (10 of 10 tries in a test), so one player counted as two. An opt-out followed by an immediate reload was lost in the same way (5 of 5 tries), and tracking started again (P-2791). A state that 0.1.0 kept in PlayerPrefs moves to `localStorage` on the first read. Without `localStorage` (some private modes and sandboxed iframes), the SDK uses PlayerPrefs.

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
