# Manalens for Unity

Game analytics for Unity 2021.3 or later: sessions, launches, game events, errors and crashes. The SDK sends [app events](https://docs.manalens.dev/sdks/app-events) to `POST {endpoint}/v0/events` with your project's publishable key. It has no dependencies and works on Windows, macOS, Linux, Android, iOS and WebGL.

Full guide: [docs.manalens.dev/sdks/unity](https://docs.manalens.dev/sdks/unity).

## Install

In Unity, open **Window > Package Manager**, click **+**, choose **Add package from git URL**, and paste:

```
https://github.com/manalens/manalens-unity.git#v0.4.0
```

Or add this line to `Packages/manifest.json` under `dependencies`:

```json
"dev.manalens.sdk": "https://github.com/manalens/manalens-unity.git#v0.4.0"
```

Keep the `#v0.4.0` tag, so your build does not change when we release a new version.

## Start

```csharp
using System.Collections.Generic;
using UnityEngine;
using ManalensSdk;

public class Analytics : MonoBehaviour
{
    void Awake()
    {
        Manalens.Init(new ManalensOptions
        {
            PublishableKey = "YOUR_PUBLISHABLE_KEY",
            Endpoint = "https://YOUR_COLLECTOR_ENDPOINT",
            Build = "571",
            Storefront = "steam",
        });
    }
}

// Anywhere in your game:
Manalens.Track("level_complete", new Dictionary<string, object> { ["level"] = 3, ["score"] = 1200 });
Manalens.Identify("player_42");
Manalens.TrackError("Save failed", new TrackErrorOptions { ErrorType = "SaveFailed", Severity = "critical" });
```

The SDK sends `game_open`, `session_start`, `session_end`, captured exceptions and `game_crash` (after a crash; not on WebGL, where a closed tab has no quit event) for you.

**Crash stacks.** In Windows, macOS, and Linux players, `game_crash` also says where the game crashed. At the next launch, the SDK reads the crash data that Unity wrote: on Windows the newest crash folder (`%TEMP%\<company>\<product>\Crashes`, file `error.log`), else the previous Player log (`Player-prev.log` next to `Application.consoleLogPath`). It adds `stack` (managed C# frames first with Mono, then native frames; the top frames, at most 1024 characters), `error_type` (for example `SIGSEGV` or `EXCEPTION_ACCESS_VIOLATION`), `message`, `fingerprint`, `crash_source` (`unity_crash_folder` or `unity_player_log`), and `symbolicated`. The SDK installs no crash handler, reads at most 256 KB, never uploads `crash.dmp`, never sends the same file twice, and reads nothing before consent. Home folders and emails are removed. Set `CaptureCrashStacks = false` to turn it off.

**Hangs.** A watchdog thread checks a heartbeat that the main thread writes once per frame (`Update`). When one frame takes longer than `HangThresholdSeconds` (default `5`; other values than `0` are clamped to 1 to 60; also in **Project Settings > Manalens**), the main thread is stuck: the SDK sends an `error` with `error_type` `game_hang`, severity `error`, and `source` `auto`, once per stuck period. The watchdog writes the hang to disk (`manalens_hang.txt` in `Application.persistentDataPath`) while the game is stuck, so it is not lost when the player kills the game. Every hang has `handled` `false` and the message "The main thread was blocked for N s" (as the web SDK). When the game recovers, the hang is sent with `hang_duration_s` (seconds, one decimal, for example `6.2`), and N is that duration. When the game was killed while stuck, the next launch sends the hang in the old session, N is the threshold, and `game_crash` gets `hang_message_id` (the `message_id` of the last hang of that session). Unity cannot read the main thread stack from another thread, so a hang has no `stack`. `SetConsent(false)` and `Reset()` also clear the session and the breadcrumbs that the watchdog keeps, so no hang with old data is saved after them. No hang is counted while the game is unfocused or paused, while the editor is paused (each has its own flag), while a debugger is attached (a breakpoint), in the first 10 frames after `Init()`, on WebGL, or on a dedicated server build without `TrackServerBuilds`. Hangs use the same error limits as other automatic errors, and `BeforeSend` can drop them. Set `HangThresholdSeconds = 0` to turn it off.

The `Basic` sample (Package Manager > Manalens > Samples) has a scene with buttons that send an event and an error.

## Traits

`Manalens.Traits()` sets traits on this player. It needs no player ID: before `Identify()`, the traits belong to this install's anonymous ID; after it, to the player. A `null` value clears a trait.

```csharp
Manalens.Traits(new Dictionary<string, object> { ["level"] = 3, ["difficulty"] = "hard" });
```

- Values are strings, numbers, bools or `null`. At most 50 keys and 8 KB of JSON.
- The protected traits `email`, `itch_user_id`, `stripe_customer_id` and `paddle_customer_id` are not sent (one warning). Only a server with the secret key can set them.
- Like `Identify()`, it waits for consent. Calls before consent are merged and sent then.

## Verify Steam ownership

`Manalens.LinkSteam()` links the Steam account that runs the game to this install. Manalens checks a Steam ticket with Steam and reads which of your apps the account owns. The player does not sign in. See [Steam ownership](https://docs.manalens.dev/features/steam-ownership) and the [Link API](https://docs.manalens.dev/api/link).

- **Steamworks.NET:** set `SteamLink = true` in the options, or call `Manalens.LinkSteam()` after `SteamAPI.Init()`. The SDK calls `SteamUser.GetAuthTicketForWebApi("manalens")`, waits for `GetTicketForWebApiResponse_t`, and sends the ticket once per launch after consent. The helper compiles with the define `MANALENS_STEAMWORKS_NET`. The package sets it when the Steamworks.NET package (`com.rlabrecque.steamworks.net`) is installed. For a copy of Steamworks.NET in `Assets`, add the define in **Player Settings > Scripting Define Symbols**. Without it, the SDK has no Steam dependency.
- **Facepunch.Steamworks or your own Steam code:** make a ticket with the identity `manalens` and pass its bytes:

```csharp
var ticket = await SteamUser.GetAuthTicketForWebApiAsync("manalens"); // Facepunch.Steamworks
Manalens.LinkSteam(ticket.Data);
```

- `Manalens.SteamLinked += (linked, status, code) => ...` gives the answer. `code` is the API error code (for example `STEAM_TICKET_INVALID` or `STEAM_NOT_CONNECTED`), or `NETWORK_ERROR`, `CONSENT_REQUIRED` or `IDENTITY_CHANGED`.
- The request goes to `<ApiEndpoint>/v0/link`. `ApiEndpoint` defaults to the `Endpoint` with `events.` changed to `api.`.
- A refused ticket is not sent again. After a `429` with `Retry-After` (60 s or less), the SDK tries once more, but not after `SetConsent(false)` or `Reset()` during the wait.
- Say in your privacy notice that your game sends a Steam ticket to Manalens to confirm ownership.

## No network

The SDK keeps events on disk and sends them in batches. When `Application.internetReachability` is `NotReachable`, the SDK starts no request: the events stay in the queue, and the wait after failed sends does not grow. When the device has a network again, the next tick (`FlushIntervalSeconds`) sends at once. Other `internetReachability` values do not prove a network, so a real network error still waits and tries again (2 s, 4 s, and so on, at most 300 s).

## BeforeSend

`BeforeSend` gets a copy of each event (track, identify, traits, error, automatic events) before it is queued. Return it (changed or not), or `null` to drop it. The SDK takes `event`, `properties`, `traits`, and `user_id` from the result. If the result changes or removes `type`, `message_id`, `anonymous_id`, `session_id`, or `context.app`, the SDK sends the original event.

```csharp
// Keep 10% of player_jump events (sampling).
var random = new System.Random();
Manalens.SetBeforeSend(e =>
    e.TryGetValue("event", out var name) && (string)name == "player_jump" && random.NextDouble() >= 0.1 ? null : e);

// Remove personal data from every event (PII scrub).
Manalens.SetBeforeSend(e =>
{
    ((Dictionary<string, object>)e["properties"]).Remove("email");
    if (e.TryGetValue("traits", out var traits)) ((Dictionary<string, object>)traits).Remove("email");
    return e;
});
```

## WebGL

A WebGL build runs in a browser, so its requests carry an `Origin` header. The collector accepts it when the page is on:

- your project domain or a subdomain (for example `play.example.com` for `example.com`);
- a host in **Project settings > Allowed origins**.

itch.io serves HTML5 builds from `html.itch.zone` and `html-classic.itch.zone`. A new project lists `*.itch.zone`, so an itch.io build works with no change. If you removed it, add `*.itch.zone` again (or click **Restore default**). For another portal, add its host (for example `*.crazygames.com`). A page on any other host gets 403. See [Allowed origins](https://docs.manalens.dev/sdks/app-events#allowed-origins).

## Layout

- `Runtime/Core`: the engine-independent SDK (no UnityEngine reference): envelopes, identity, sessions, queue, retries, consent, errors.
- `Runtime/Unity`: the Unity layer: the `Manalens` static API, a hidden MonoBehaviour (Update, focus, pause, quit, log capture), UnityWebRequest, and the WebGL storage (`localStorage` through `WebGL/ManalensStorage.jslib`, else PlayerPrefs).
- `Samples~/Basic`: a bootstrap component.
- `Tests~`: .NET projects that build and test the core outside Unity (`dotnet test`), fixtures and a local end-to-end runner. Unity does not import folders that end in `~`.
- `.github/workflows`: CI (`ci.yml`) runs `dotnet test 'Tests~/Manalens.Core.Tests'`, the Unity layer compile check (`Tests~/UnityStubs`), and the `.meta` check on each pull request. `release.yml` makes a release from a `v*` tag.

To change the SDK, see [CONTRIBUTING.md](CONTRIBUTING.md): how to test, how to release, and the commit rules.

## Releases

Each [release](https://github.com/manalens/manalens-unity/releases) has `dev.manalens.sdk-<version>.tgz` (the package without `Tests~`, for **Add package from tarball**), `SHA256SUMS`, and the changes from `CHANGELOG.md`. GitHub Actions builds the tarball from the tagged commit and attests its build provenance. To check a download:

```bash
sha256sum --check --ignore-missing SHA256SUMS
gh attestation verify dev.manalens.sdk-<version>.tgz --repo manalens/manalens-unity
```

## License

MIT
