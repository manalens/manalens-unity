using System;
using System.Collections.Generic;
using System.IO;
using ManalensSdk;
using ManalensSdk.Editor;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Batch-mode check of Project Settings > Manalens (G-2532), against a real
/// collector:
///   1. creates Assets/Resources/ManalensSettings.asset with the key,
///   2. Play mode: the SDK starts by itself (no Init call), uses the settings,
///      and sends; the Play mode state (last send, recent events) is filled,
///   3. edit mode: "Send test event" reaches the collector (HTTP 202),
///   4. deletes the asset and exits with 0 or 1.
///
///   MANALENS_ENDPOINT=http://127.0.0.1:3101 MANALENS_PUBLISHABLE_KEY=mana_pk_... \
///   Unity -batchmode -projectPath Tests~/UnityProject -executeMethod SetupCheck.Run -logFile -
///
/// Prints MANALENS_SETUP {json}.
/// </summary>
[InitializeOnLoad]
public static class SetupCheck
{
    const string StepKey = "ManalensSetupCheck.Step";
    const string FailuresKey = "ManalensSetupCheck.Failures";
    const string ResultKey = "ManalensSetupCheck.Result";
    const string AssetPath = "Assets/Resources/ManalensSettings.asset";
    static double _deadline;
    static bool _tracked;

    static SetupCheck()
    {
        var step = SessionState.GetString(StepKey, "");
        if (step == "play") EditorApplication.update += InPlayMode;
        // Leaving Play mode does not reload the scripts: wait for the state change.
        EditorApplication.playModeStateChanged += state =>
        {
            if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetString(StepKey, "") == "edit") EditorApplication.delayCall += InEditMode;
        };
    }

    public static void Run()
    {
        var key = Environment.GetEnvironmentVariable("MANALENS_PUBLISHABLE_KEY") ?? "";
        var endpoint = Environment.GetEnvironmentVariable("MANALENS_ENDPOINT") ?? "";
        if (key == "" || endpoint == "") Finish("set MANALENS_ENDPOINT and MANALENS_PUBLISHABLE_KEY");
        Directory.CreateDirectory("Assets/Resources");
        var settings = ScriptableObject.CreateInstance<ManalensSettings>();
        settings.PublishableKey = key;
        settings.Endpoint = endpoint;
        settings.Build = "setup-check";
        AssetDatabase.CreateAsset(settings, AssetPath);
        AssetDatabase.SaveAssets();
        SessionState.SetString(FailuresKey, "");
        SessionState.SetString(StepKey, "play");
        EditorApplication.EnterPlaymode();
    }

    static void InPlayMode()
    {
        if (!EditorApplication.isPlaying) return;
        if (_deadline == 0) _deadline = EditorApplication.timeSinceStartup + 20;
        if (EditorApplication.timeSinceStartup > _deadline)
        {
            Fail("Play mode: timed out (initialized " + Manalens.IsInitialized + ", tracking " + Manalens.IsTracking + ", last send \"" + Manalens.LastSendResult + "\")");
            LeavePlayMode();
            return;
        }
        if (!Manalens.IsTracking) return;
        if (!_tracked)
        {
            _tracked = true;
            Debug.Log("MANALENS_SETUP_STEP play: started by itself, " + Manalens.RecentEvents.Length + " events");
            if (Array.FindIndex(Manalens.RecentEvents, json => json.Contains("\"build\":\"setup-check\"")) < 0) Fail("Play mode: the events do not have the Build of the settings");
            if (Array.FindIndex(Manalens.RecentEvents, json => json.Contains("\"game_open\"")) < 0) Fail("Play mode: no game_open in the recent events");
            Manalens.Track(TestEvent.Name, new Dictionary<string, object> { ["source"] = "setup_check" });
            Manalens.Flush();
            return;
        }
        if (Manalens.IsSending || Manalens.QueueSize > 0 || Manalens.LastSendResult == "") return;
        SessionState.SetString(ResultKey, "play: " + Manalens.LastSendResult);
        Debug.Log("MANALENS_SETUP_STEP play: " + Manalens.LastSendResult);
        if (!Manalens.LastSendResult.StartsWith("HTTP 202")) Fail("Play mode: last send " + Manalens.LastSendResult);
        LeavePlayMode();
    }

    static void LeavePlayMode()
    {
        EditorApplication.update -= InPlayMode;
        SessionState.SetString(StepKey, "edit");
        EditorApplication.ExitPlaymode();
    }

    static void InEditMode()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            EditorApplication.delayCall += InEditMode;
            return;
        }
        SessionState.SetString(StepKey, "");
        var settings = AssetDatabase.LoadAssetAtPath<ManalensSettings>(AssetPath);
        if (settings == null)
        {
            Finish("the settings asset is gone");
            return;
        }
        Debug.Log("MANALENS_SETUP_STEP edit: sending the test event");
        ManalensSettingsProvider.SendFromEditor(settings);
        _deadline = EditorApplication.timeSinceStartup + 20;
        EditorApplication.update += WaitForTest;
    }

    static void WaitForTest()
    {
        if (ManalensSettingsProvider.TestBusy && EditorApplication.timeSinceStartup < _deadline) return;
        EditorApplication.update -= WaitForTest;
        var result = ManalensSettingsProvider.TestResult;
        SessionState.SetString(ResultKey, SessionState.GetString(ResultKey, "") + " | editor: " + result);
        if (!result.StartsWith("Received")) Fail("edit mode: " + result);
        Finish(null);
    }

    static void Fail(string message)
    {
        var all = SessionState.GetString(FailuresKey, "");
        SessionState.SetString(FailuresKey, all == "" ? message : all + "\n" + message);
    }

    static void Finish(string error)
    {
        if (error != null) Fail(error);
        AssetDatabase.DeleteAsset(AssetPath);
        if (AssetDatabase.FindAssets("", new[] { "Assets/Resources" }).Length == 0) AssetDatabase.DeleteAsset("Assets/Resources");
        var failures = SessionState.GetString(FailuresKey, "");
        var ok = failures == "";
        Debug.Log("MANALENS_SETUP {\"ok\":" + (ok ? "true" : "false") + ",\"result\":\"" + SessionState.GetString(ResultKey, "").Replace("\"", "'") + "\",\"failures\":\"" + failures.Replace("\"", "'").Replace("\n", "; ") + "\"}");
        EditorApplication.Exit(ok ? 0 : 1);
    }
}
