using System.Collections.Generic;
using UnityEngine;
using ManalensSdk;

/// <summary>
/// Buttons for the sample scene: send an event, log an error (the SDK
/// captures it) and open a website with the handoff parameter. Remove it
/// from your game.
/// </summary>
public class AnalyticsDemoPanel : MonoBehaviour
{
    int _level = 1;

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(16, 16, 320, 260));
        GUILayout.Label(Manalens.IsInitialized ? $"Manalens: {Manalens.QueueSize} queued, session {Manalens.SessionId}" : "Manalens is not started");
        if (GUILayout.Button($"Track level_complete (level {_level})"))
        {
            Manalens.Track("level_complete", new Dictionary<string, object> { ["level"] = _level, ["score"] = Random.Range(100, 1000) });
            _level++;
        }
        if (GUILayout.Button("Log an error (captured)")) Debug.LogError("Sample error from the demo panel");
        if (GUILayout.Button("Send now")) Manalens.Flush();
        if (GUILayout.Button("Open website")) GetComponent<AnalyticsBootstrap>()?.OpenWiki();
        GUILayout.EndArea();
    }
}
