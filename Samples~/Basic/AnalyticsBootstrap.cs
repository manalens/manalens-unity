using System.Collections.Generic;
using UnityEngine;
using UntitledAnalyticsSdk;

/// <summary>
/// Add this component to a GameObject in your first scene. Fill in the
/// publishable key and the endpoint in the Inspector. The SDK sends
/// game_open, session_start and session_end for you.
/// </summary>
public class AnalyticsBootstrap : MonoBehaviour
{
    [Tooltip("The project's Publishable key (ua_pk_...). Never a secret key or an API key.")]
    public string publishableKey = "YOUR_PUBLISHABLE_KEY";

    [Tooltip("The collector URL, without /v0/events.")]
    public string endpoint = "https://YOUR_COLLECTOR_ENDPOINT";

    public string build = "1";

    [Tooltip("steam, itch, epic, gog, app_store, play or other")]
    public string storefront = "steam";

    [Tooltip("Also send events from Play mode in the editor (use a test project key).")]
    public bool sendFromEditor;

    void Awake()
    {
        UntitledAnalytics.SendFailed += (status, dropped) => Debug.LogWarning($"Analytics dropped {dropped} events (HTTP {status})");
        UntitledAnalytics.Init(new UntitledAnalyticsOptions
        {
            PublishableKey = publishableKey,
            Endpoint = endpoint,
            Build = build,
            Storefront = storefront,
            Enabled = sendFromEditor || !Application.isEditor,
            Debug = Application.isEditor,
        });
    }

    void Start()
    {
        UntitledAnalytics.Track("level_start", new Dictionary<string, object> { ["level"] = 1, ["mode"] = "story" });
    }

    /// <summary>Call from a UI button: opens your website and joins the browser to this player.</summary>
    public void OpenWiki()
    {
        Application.OpenURL(UntitledAnalytics.LinkUrl("https://example.com/wiki"));
    }
}
