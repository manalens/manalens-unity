# Manalens for Unity

Game analytics for Unity 2021.3 or later: sessions, launches, game events, errors and crashes. The SDK sends [app events](https://docs.manalens.dev/sdks/app-events) to `POST {endpoint}/v0/events` with your project's publishable key. It has no dependencies and works on Windows, macOS, Linux, Android, iOS and WebGL.

Full guide: [docs.manalens.dev/sdks/unity](https://docs.manalens.dev/sdks/unity).

## Install

In Unity, open **Window > Package Manager**, click **+**, choose **Add package from git URL**, and paste:

```
https://github.com/manalens/manalens-unity.git#v0.1.6
```

Or add this line to `Packages/manifest.json` under `dependencies`:

```json
"dev.manalens.sdk": "https://github.com/manalens/manalens-unity.git#v0.1.6"
```

Keep the `#v0.1.6` tag, so your build does not change when we release a new version.

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

The SDK sends `game_open`, `session_start`, `session_end`, captured exceptions and `app_unclean_exit` (after a crash; not on WebGL, where a closed tab has no quit event) for you.

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
