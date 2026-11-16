# Design notes

The Unity SDK is a C# port of the Godot SDK (`sdks/godot`). Same behavior, same wire format, same event names. This file records what we took from other Unity SDKs and why.

## What other Unity SDKs do

We read the source or the docs of these SDKs (November 2026). We took ideas and API shapes, not code.

| SDK | License | What we looked at |
| --- | --- | --- |
| Unity Analytics (UGS, `com.unity.services.analytics`) | Unity terms, not open source | Automatic events, consent, flush, cache on quit |
| GameAnalytics (`GA-SDK-UNITY`) | MIT | Settings asset, sessions, error capture, event types |
| mixpanel-unity | Apache 2.0 | Queue in PlayerPrefs, flush coroutine, own JSON writer |
| PostHog Unity (`posthog-unity`) | MIT | Defaults, file queue, retry classes, quit handling |
| Sentry Unity | MIT | Log capture, stack parsing, dedupe, crashed sessions, domain reload |
| Amplitude Unity | MIT | A bridge to native SDKs only; not a model |

Findings:

- **Unity Analytics** sends `gameStarted`, `clientDevice` and `newPlayer` at session start, `gameRunning` as a heartbeat each minute, and `gameEnded` on quit (lost when the OS kills the app). It flushes each 60 s and caches unsent events to a file on quit. Consent is `StartDataCollection()` per session (SDK 6.1+: the engine consent API).
- **GameAnalytics** keeps settings in a ScriptableObject in `Resources/`. It captures errors with `Application.logMessageReceived`, with a hard cap of 10 per run and no dedupe. It blocks the quit with `Thread.Sleep(1500)` to send `session_end`.
- **mixpanel-unity** stores each event under its own PlayerPrefs key and flushes from a coroutine each 60 s, with exponential backoff. It writes JSON itself, because `JsonUtility` cannot write dictionaries.
- **PostHog Unity** is the closest model: flush at 20 events or 30 s, a queue of 1000, batches of 50, a `BeforeSend` hook, a file per event (PlayerPrefs on WebGL), retry on network errors, 408, 429 and 5xx, and a quit delay through `Application.wantsToQuit` with a timeout.
- **Sentry Unity** captures errors with `Application.logMessageReceivedThreaded` (and a log handler for exception objects). It skips its own log lines, throttles repeated errors, parses `Class:Method (args) (at file:line)` frames, and marks a session that did not end cleanly as crashed at the next start. It resets static state at `SubsystemRegistration` for projects with domain reload turned off.

## What we adopted

1. **Pure core plus a thin Unity layer.** `Runtime/Core` has no UnityEngine reference (`noEngineReferences` in its asmdef) and gets storage, HTTP, clock and log through interfaces. It builds and runs under `dotnet test`. `Runtime/Unity` is about 400 lines: the static API, a hidden `DontDestroyOnLoad` MonoBehaviour, `UnityWebRequest`, PlayerPrefs.
2. **Main thread I/O.** `UnityWebRequest` must run on the main thread. `Track()` may be called from any thread (one lock), but the SDK sends only from `Update()` and the lifecycle hooks. `Flush()` from another thread waits for the next frame.
3. **Log capture from any thread** (Sentry). `Application.logMessageReceivedThreaded` only copies the message into a capped list. `Update()` sends at most 5 per frame. The SDK skips its own `UntitledAnalytics:` lines, so it can never loop.
4. **Error limits** as the Godot SDK and Sentry: one fingerprint at most once a second, at most 20 errors in 10 seconds, at most 50 automatic errors in one session, `errors_dropped` on `session_end`. Fingerprint: SHA-1 of error type, file and function, or of the message without numbers and quoted values. Same function as the Godot SDK.
5. **Crash detection without native code** (Sentry's abnormal session, Godot SDK): the open session is saved with a `focused` flag. If the next launch finds it open and focused, it sends `app_unclean_exit` in that session. A mobile app that the OS killed in the background is not a crash.
6. **Quit** (PostHog): in a desktop player, `Application.wantsToQuit` returns false once, a coroutine sends `session_end` and the queue for at most `QuitTimeoutSeconds`, then quits. Never in the editor, where `Application.Quit()` does nothing. `Application.quitting` always ends the session and saves the queue. On mobile, pause saves everything and starts a send; the next launch sends the rest.
7. **Storage**: files in `Application.persistentDataPath` (written to a temporary file first). On WebGL, PlayerPrefs (IndexedDB), because files there need a manual sync (PostHog does the same).
8. **Own JSON writer** with the invariant culture (Mixpanel, PostHog): numbers stay numbers, `1.5` never becomes `1,5`, booleans stay booleans.
9. **Domain reload off**: static state resets at `RuntimeInitializeLoadType.SubsystemRegistration` (Sentry).
10. **Consent fails closed** (security review). Storage tells "missing" apart from "exists but unreadable"; the state file has a header and an end line, so a cut-off write does not parse. Only a missing state is a first run. An unreadable or damaged state keeps the SDK off and is never overwritten (a damaged one is copied aside; `SetConsent(true)` starts a new state). An opt-out also writes a marker file that wins over the state. A failed opt-out write keeps the SDK off and is retried each tick. All writes go to a temporary file and are renamed.
11. **`Enabled = !Application.isEditor`** in the docs, so Play mode tests do not mix into real reports (Sentry's `CaptureInEditor`, Godot SDK `enabled`).

## What we did not adopt (yet)

- **Settings asset in `Resources/`** (GameAnalytics, Sentry). Code-only `Init()` is fewer moving parts and keeps one source of truth with the Godot SDK. An asset can be added later without an API change.
- **Device events** (`clientDevice`, battery, RAM, GPU). The collector schema has no fields for them, and we send no hardware details (same as the Godot SDK).
- **Heartbeat events** (`gameRunning`). Sessions already stay alive while the game has focus.
- **Halving the batch on 413** (PostHog). Batches are at most 50 small events; a 413 is a 4xx and drops the batch, as in the Godot SDK.
- **A log handler wrapper** for exception objects (Sentry, PostHog). `logMessageReceivedThreaded` gives the type, message and stack, which is all the `error` event needs. `TrackError(Exception)` covers caught exceptions.
- **UUIDv7 session IDs** (PostHog). UUIDv4, same as the other SDKs.

## Not verified

The Unity layer (`Runtime/Unity`) compiles against UnityEngine stand-ins (`Tests~/UnityStubs`), but it has not run in the Unity Editor or on a device. Check in a real project before publishing: Package Manager import from the git URL, `Init` in Play mode, focus and pause events on Android and iOS, the quit delay in a Windows and a macOS player, WebGL PlayerPrefs storage, IL2CPP stripping.
