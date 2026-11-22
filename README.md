# Manalens for Unity

Game analytics for Unity 2021.3 or later: sessions, launches, game events, errors and crashes. The SDK sends app events (`docs/sdks/app-events.mdx`) to `POST {endpoint}/v0/events` with your project's publishable key. It has no dependencies and works on Windows, macOS, Linux, Android, iOS and WebGL.

Full guide: `docs/sdks/unity.mdx` in the Manalens repository.

## Install

In Unity, open **Window > Package Manager**, click **+**, choose **Add package from tarball** and pick the `.tgz` file (or **Add package from disk** for a folder).

During early access the package is available on request from support@manalens.dev. The SDK lives in the private Manalens repository for now, so there is no public git URL. It moves to its own public repository when it is published (P-2481).

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
            Enabled = !Application.isEditor,
        });
    }
}

// Anywhere in your game:
Manalens.Track("level_complete", new Dictionary<string, object> { ["level"] = 3, ["score"] = 1200 });
Manalens.Identify("player_42");
Manalens.TrackError("Save failed", new TrackErrorOptions { ErrorType = "SaveFailed", Severity = "critical" });
```

The SDK sends `game_open`, `session_start`, `session_end`, captured exceptions and `app_unclean_exit` (after a crash) for you.

## Layout

- `Runtime/Core`: the engine-independent SDK (no UnityEngine reference): envelopes, identity, sessions, queue, retries, consent, errors.
- `Runtime/Unity`: the Unity layer: the `Manalens` static API, a hidden MonoBehaviour (Update, focus, pause, quit, log capture), UnityWebRequest and PlayerPrefs.
- `Samples~/Basic`: a bootstrap component.
- `Tests~`: .NET projects that build and test the core outside Unity (`dotnet test`), fixtures and a local end-to-end runner. Unity does not import folders that end in `~`.

## License

MIT
