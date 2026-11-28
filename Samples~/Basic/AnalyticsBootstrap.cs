using System.Collections.Generic;
using UnityEngine;
using ManalensSdk;

/// <summary>
/// Add this component to a GameObject in your first scene. Fill in the
/// publishable key and the endpoint in the Inspector. The SDK sends
/// game_open, session_start and session_end for you.
/// </summary>
public class AnalyticsBootstrap : MonoBehaviour
{
    [Tooltip("The project's Publishable key (mana_pk_...). Never a secret key or an API key.")]
    public string publishableKey = "YOUR_PUBLISHABLE_KEY";

    [Tooltip("The collector URL, without /v0/events.")]
    public string endpoint = "https://YOUR_COLLECTOR_ENDPOINT";

    public string build = "1";

    [Tooltip("steam, itch, epic, gog, app_store, play or other")]
    public string storefront = "steam";

    [Tooltip("The build sent from Play mode in the editor. Editor and live data go to the same project, so filter by build to tell them apart.")]
    public string editorBuild = "dev";

    void Awake()
    {
        Manalens.SendFailed += (status, dropped) => Debug.LogWarning($"Analytics dropped {dropped} events (HTTP {status})");
        Manalens.Init(new ManalensOptions
        {
            PublishableKey = publishableKey,
            Endpoint = endpoint,
            Build = Application.isEditor ? editorBuild : build,
            Storefront = storefront,
            Debug = Application.isEditor,
        });
    }

    void Start()
    {
        Manalens.Track("level_start", new Dictionary<string, object> { ["level"] = 1, ["mode"] = "story" });
    }

    /// <summary>
    /// Call from a UI button: opens a link to your website that keeps this player.
    /// The link holds the current time and works for 10 minutes, so make it when the player clicks.
    /// </summary>
    public void OpenWiki()
    {
        Application.OpenURL(Manalens.GetHandoffUrl("https://example.com/wiki"));
    }
}
