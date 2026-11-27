# Changelog

## 0.5.0 (2026-11-27)

- Crash stacks (P-2831): in Windows, macOS, and Linux players, `app_crash` now says where the game crashed. At the next launch, the SDK reads the crash data that Unity wrote for the crashed run (Windows: the newest crash folder's `error.log`; all desktop players: `Player-prev.log`) and adds `stack`, `error_type`, `message`, `fingerprint`, `crash_source`, and `symbolicated`. Mono players give the managed C# frames first. No crash handler; at most 256 KB read; `crash.dmp` is never sent; each file once; nothing before consent. New option `CaptureCrashStacks` (default `true`) and `AppEnvironment.CrashFiles` for a custom engine layer.

## 0.4.0 (2026-11-27)

- A `413` (batch too large) no longer drops the batch (P-2853). For a batch of more than 1 event, the SDK keeps the events and sends again at once with half the batch. It does not back off and does not count a failure. A `413` for 1 event drops that event and raises `SendFailed(413, 1)`, as the other refused `4xx`. After a `2xx`, batches use the normal size (`FlushAt`, at most 50) again.
- No send while the device has no network (P-2853). When `Application.internetReachability` is `NotReachable`, the SDK starts no request, keeps the events, and does not count a failure or back off. When the device has a network again, the next tick sends at once (before, a long time offline made the SDK wait up to 300 s). A real network error during a send still backs off. New `AnalyticsClient.IsOffline` (a `Func<bool>`) for a custom engine layer.

## 0.3.0 (2026-11-27)

- Breaking: standard names for error and crash events (the same in the Unity, Godot, and Unreal SDKs).
  - The crash event `app_unclean_exit` is now `app_crash`. Its properties (`previous_session_id`, `previous_build`, `previous_app_version`) and the rule that sends it do not change.
  - The `source` property of an `error` event is now `manual` (a `TrackError` call) or `auto` (any automatic capture). The old value `unity_log` is now `auto`. The engine is in `context.app.engine`.
  - `log_type` (`exception`, `error`, `assert`, `warning`) does not change. The per-session cap (`MaxAutoErrorsPerSession`) applies to `auto` errors only, as before.

## 0.2.0 (2026-11-27)

- New `Manalens.Traits(traits)` (P-2814): set traits on this player, with or without a player ID. Before `Identify()` the traits belong to the anonymous ID of this install. The protected traits `email`, `itch_user_id`, `stripe_customer_id`, and `paddle_customer_id` are not sent (one warning). Calls before consent are merged and sent after consent.
- New `Manalens.LinkSteam(byte[] ticket)`, `Manalens.LinkSteam()`, the option `SteamLink`, and the event `SteamLinked` (P-2815). The SDK posts a Steam Web API ticket (identity `manalens`) to `/v0/link` on the API host (new option `ApiEndpoint`; default: the `Endpoint` with `events.` changed to `api.`), after consent, from `Update()`. With Steamworks.NET (define `MANALENS_STEAMWORKS_NET`, set by the package's version define), the SDK asks Steam for the ticket itself. A refusal is not sent again; a `429` with `Retry-After` of 60 s or less is tried once more, but not after `SetConsent(false)` or `Reset()` during the wait.
- The WebGL and player transport now reads the response body of a `5xx` too, so the SDK can log its error code.
- The shared fixture scenario has a `traits` event.

## 0.1.6 (2026-11-26)

- Console values (P-2776). `context.app.platform` is now `ps5` (PS5), `xbox_series` (GameCoreXboxSeries), `xbox_one` (GameCoreXboxOne and the old XboxOne player), or `switch` (Switch) instead of `other`. New option `Platform`: empty (default) detects the platform; set it when the detection cannot name the console, for example `"switch2"` in a Nintendo Switch 2 build. `Storefront` also accepts `psn`, `xbox`, and `nintendo`.

## 0.1.5 (2026-11-26)

- Dedicated server builds (G-2533): a player built with the `UNITY_SERVER` define, or a player started with `-batchmode`, sends no `game_open`, `session_start`, `session_end`, or `app_unclean_exit`, and does not count as an install. The SDK logs one line that says why. `Track()` and `Identify()` still work. The Unity Editor is never a server build, also in batch mode (test runs, builds), so Play mode and **Send test event** do not change. The new option `TrackServerBuilds` (default `false`, also on the settings asset) sends the automatic events from a server build. New `AppEnvironment.IsServerBuild` for a custom engine layer. See the new docs page "Multiplayer and dedicated servers".

## 0.1.4 (2026-11-26)

- Each request sends `sent_at`, the device clock at the time of the send. The collector uses it to correct the event times, so a device clock that is fast or slow no longer gives wrong times or refused events (P-2799).
- The SDK follows `Retry-After` (P-2794). After a retried response (network error, `408`, `429`, `5xx`), it waits at least the `Retry-After` value (seconds or an HTTP date), at most 1 hour. Without the header, the backoff stays as before (2, 4, 8 ... seconds with jitter, at most 5 minutes). The collector sends `Retry-After` with a rate limit `429` and with a `503`, so the SDK no longer retries before the collector is ready.
- New optional interface `IRetryAfterTransport` (extends `ITransport`): a `Send` overload whose callback also gives the raw `Retry-After` header. The built-in `UnityWebRequest` transport implements it. This change is backward compatible: `ITransport` does not change, and a custom transport that implements only `ITransport` still compiles and works, with the backoff only. To follow `Retry-After` with a custom transport, implement `IRetryAfterTransport` too.
- New `AnalyticsClient.ParseRetryAfter(header, nowUnixSeconds)` and `AnalyticsClient.MaxRetryAfterSeconds` (3600).
- Changed: `BeforeSend` and `SetBeforeSend()` now run on every event (track, identify, error, and the automatic events), not only on errors. The callback gets a copy of the whole event (`type`, `event`, `properties`, `traits`, `user_id`, ...) instead of the error properties; return it (changed or not), or `null` to drop it. The SDK takes `event`, `properties`, `traits`, and `user_id` from the result. If the result changes or removes `type`, `message_id`, `anonymous_id`, `session_id`, or `context.app`, the original event is sent. Errors pass the error limits first. A callback that throws drops the event. Update a callback that read `props["message"]` to read the `properties` of the event (G-2527).
- Privacy: `SetConsent(false)` and `Reset()` now clear the breadcrumbs (the last event names), the error limits, and the dropped-error count. Event names from before a withdrawal or a sign-out no longer reach a later `error` event. `Reset()` sends the old `errors_dropped` in the old `session_end` first. `SetConsent(false)` also drops the log messages that wait for the next `Update()` (P-2783).
- Privacy: errors captured from `Application.logMessageReceived` are cleaned before they are queued, with the rules of the Unreal plugin. The home folder and `/Users/<name>`, `/home/<name>`, `X:\Users\<name>` become `~`. URLs lose their user, query, and fragment. Emails become `[email]`. The values after `key`, `token`, `secret`, `password`, and `authorization` with `=` or `:` (also JSON, and quoted values up to the closing quote) and after `bearer`, JWTs, base64 runs of 24 or more characters with `+` or a trailing `=`, and runs of 24 or more letters, digits, `_`, and `-` become `[redacted]`. The exception type at the start of a message is kept. The fingerprint is computed from the cleaned text. `TrackError()` text is not changed (P-2783).

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
