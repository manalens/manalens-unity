# Untitled Analytics for Unity

Game analytics for Unity 2021.3 or later: sessions, launches, game events, errors and crashes. The SDK sends app events (`docs/sdks/app-events.mdx`) to `POST {endpoint}/v0/events` with your project's publishable key. It has no dependencies and works on Windows, macOS, Linux, Android, iOS and WebGL.

Full guide: `docs/sdks/unity.mdx` in the Untitled Analytics repository.

## Install

In Unity, open **Window > Package Manager**, click **+**, choose **Add package from git URL** and enter:

```
https://github.com/manalens/manalens.git?path=sdks/unity
```

The SDK lives in the Untitled Analytics monorepo for now. It moves to its own public repository when it is published.

## Start

```csharp
using System.Collections.Generic;
using UnityEngine;
using UntitledAnalyticsSdk;

public class Analytics : MonoBehaviour
{
    void Awake()
    {
        UntitledAnalytics.Init(new UntitledAnalyticsOptions
        {
            PublishableKey = "YOUR_PUBLISHABLE_KEY",
            Endpoint = "https://YOUR_COLLECTOR_ENDPOINT",
            Build = "571",
            Storefront = "steam",
            Enabled = !Application.isEditor,
        });
    }
}

// Anywhere in your game:
UntitledAnalytics.Track("level_complete", new Dictionary<string, object> { ["level"] = 3, ["score"] = 1200 });
UntitledAnalytics.Identify("player_42");
UntitledAnalytics.TrackError("Save failed", new TrackErrorOptions { ErrorType = "SaveFailed", Severity = "critical" });
```

The SDK sends `game_open`, `session_start`, `session_end`, captured exceptions and `app_unclean_exit` (after a crash) for you.

## Layout

- `Runtime/Core`: the engine-independent SDK (no UnityEngine reference): envelopes, identity, sessions, queue, retries, consent, errors.
- `Runtime/Unity`: the Unity layer: the `UntitledAnalytics` static API, a hidden MonoBehaviour (Update, focus, pause, quit, log capture), UnityWebRequest and PlayerPrefs.
- `Samples~/Basic`: a bootstrap component.
- `Tests~`: .NET projects that build and test the core outside Unity (`dotnet test`), fixtures and a local end-to-end runner. Unity does not import folders that end in `~`.

## License

MIT
