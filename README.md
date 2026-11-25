# Manalens for Unity

Game analytics for Unity 2021.3 or later: sessions, launches, game events, errors and crashes. The SDK sends [app events](https://docs.manalens.dev/sdks/app-events) to `POST {endpoint}/v0/events` with your project's publishable key. It has no dependencies and works on Windows, macOS, Linux, Android, iOS and WebGL.

Full guide: [docs.manalens.dev/sdks/unity](https://docs.manalens.dev/sdks/unity).

## Install

In Unity, open **Window > Package Manager**, click **+**, choose **Add package from git URL**, and paste:

```
https://github.com/manalens/manalens-unity.git#v0.1.0
```

Or add this line to `Packages/manifest.json` under `dependencies`:

```json
"dev.manalens.sdk": "https://github.com/manalens/manalens-unity.git#v0.1.0"
```

Keep the `#v0.1.0` tag, so your build does not change when we release a new version.

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

## Layout

- `Runtime/Core`: the engine-independent SDK (no UnityEngine reference): envelopes, identity, sessions, queue, retries, consent, errors.
- `Runtime/Unity`: the Unity layer: the `Manalens` static API, a hidden MonoBehaviour (Update, focus, pause, quit, log capture), UnityWebRequest, and the WebGL storage (`localStorage` through `WebGL/ManalensStorage.jslib`, else PlayerPrefs).
- `Samples~/Basic`: a bootstrap component.
- `Tests~`: .NET projects that build and test the core outside Unity (`dotnet test`), fixtures and a local end-to-end runner. Unity does not import folders that end in `~`.

## License

MIT
