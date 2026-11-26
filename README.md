# Manalens for Unity

Game analytics for Unity 2021.3 or later: sessions, launches, game events, errors and crashes. The SDK sends [app events](https://docs.manalens.dev/sdks/app-events) to `POST {endpoint}/v0/events` with your project's publishable key. It has no dependencies and works on Windows, macOS, Linux, Android, iOS and WebGL.

Full guide: [docs.manalens.dev/sdks/unity](https://docs.manalens.dev/sdks/unity).

## Install

In Unity, open **Window > Package Manager**, click **+**, choose **Add package from git URL**, and paste:

```
https://github.com/manalens/manalens-unity.git#v0.1.4
```

Or add this line to `Packages/manifest.json` under `dependencies`:

```json
"dev.manalens.sdk": "https://github.com/manalens/manalens-unity.git#v0.1.4"
```

Keep the `#v0.1.4` tag, so your build does not change when we release a new version.

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

## BeforeSend

`BeforeSend` gets a copy of each event (track, identify, error, automatic events) before it is queued. Return it (changed or not), or `null` to drop it. The SDK takes `event`, `properties`, `traits`, and `user_id` from the result. If the result changes or removes `type`, `message_id`, `anonymous_id`, `session_id`, or `context.app`, the SDK sends the original event.

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

## Releases

Each [release](https://github.com/manalens/manalens-unity/releases) has `dev.manalens.sdk-<version>.tgz` (the package without `Tests~`, for **Add package from tarball**), `SHA256SUMS`, and the changes from `CHANGELOG.md`. GitHub Actions builds the tarball from the tagged commit and attests its build provenance. To check a download:

```bash
sha256sum --check --ignore-missing SHA256SUMS
gh attestation verify dev.manalens.sdk-<version>.tgz --repo manalens/manalens-unity
```

## License

MIT
