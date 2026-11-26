using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace ManalensSdk.Editor
{
    /// <summary>
    /// Project Settings > Manalens (also Tools > Manalens > Settings): the
    /// settings asset, the key check, "Send test event", and in Play mode the
    /// SDK state, the queue, the last send result and the recent events.
    /// Editor only: this assembly is not in a player build.
    /// </summary>
    public static class ManalensSettingsProvider
    {
        const string SettingsPath = "Project/Manalens";
        const string AssetPath = "Assets/Resources/ManalensSettings.asset";
        const string AppUrl = "https://app.manalens.dev/";
        const string DocsUrl = "https://docs.manalens.dev/sdks/unity";
        static readonly string[] Fields = { "PublishableKey", "Endpoint", "AutoInitialize", "ConsentRequired", "EnabledInEditor", "AppVersion", "Build", "Storefront", "Debug" };

        static UnityWebRequest _request;
        static string _testResult = "";
        static bool _showEvents;
        static Vector2 _eventsScroll;
        static SettingsProvider _provider;

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            _provider = new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "Manalens",
                guiHandler = _ => OnGui(),
                keywords = new HashSet<string>(new[] { "Manalens", "analytics", "publishable key", "test event", "endpoint" }),
                activateHandler = (_, __) => EditorApplication.update += RepaintWhilePlaying,
                deactivateHandler = () => EditorApplication.update -= RepaintWhilePlaying,
            };
            return _provider;
        }

        [MenuItem("Tools/Manalens/Settings")]
        static void OpenSettings() => SettingsService.OpenProjectSettings(SettingsPath);

        static double _lastRepaint;

        static void RepaintWhilePlaying()
        {
            if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup - _lastRepaint < 0.5) return;
            _lastRepaint = EditorApplication.timeSinceStartup;
            _provider?.Repaint();
        }

        /// <summary>The settings asset: Assets/Resources/ManalensSettings.asset, or another one in a Resources folder.</summary>
        static ManalensSettings Find()
        {
            var asset = AssetDatabase.LoadAssetAtPath<ManalensSettings>(AssetPath);
            if (asset != null) return asset;
            foreach (var guid in AssetDatabase.FindAssets("t:" + nameof(ManalensSettings)))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.Replace('\\', '/').Contains("/Resources/") && Path.GetFileNameWithoutExtension(path) == ManalensSettings.ResourceName)
                    return AssetDatabase.LoadAssetAtPath<ManalensSettings>(path);
            }
            return null;
        }

        static void OnGui()
        {
            var settings = Find();
            EditorGUILayout.Space();
            if (settings == null)
            {
                EditorGUILayout.HelpBox("No settings yet. Create them to set the publishable key here, without code. They are saved in " + AssetPath + ", so player builds have them.", MessageType.Info);
                if (GUILayout.Button("Create settings", GUILayout.Width(160))) settings = CreateAsset();
                if (settings == null)
                {
                    Links();
                    return;
                }
            }

            var serialized = new SerializedObject(settings);
            serialized.Update();
            foreach (var field in Fields) EditorGUILayout.PropertyField(serialized.FindProperty(field));
            serialized.ApplyModifiedProperties();

            EditorGUILayout.Space();
            var refusal = Keys.PublishableKeyRefusal((settings.PublishableKey ?? "").Trim());
            if (refusal == null) EditorGUILayout.HelpBox("Key: OK (publishable key).", MessageType.None);
            else EditorGUILayout.HelpBox("Key: " + refusal + ".", string.IsNullOrWhiteSpace(settings.PublishableKey) ? MessageType.Info : MessageType.Error);
            EditorGUILayout.LabelField("Start", refusal != null ? "Not until you set the key or call Manalens.Init(options)."
                : settings.AutoInitialize ? "Automatic, after the first scene loads (unless your code calls Manalens.Init first)."
                : "AutoInitialize is off: call Manalens.Init() in your code.", EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Test", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                var playing = EditorApplication.isPlaying && Manalens.IsTracking;
                using (new EditorGUI.DisabledScope(_request != null || refusal != null || string.IsNullOrWhiteSpace(settings.Endpoint)))
                {
                    if (GUILayout.Button(new GUIContent("Send test event", playing
                        ? "Play mode: tracks manalens_test_event with the running SDK and sends the queue."
                        : "Sends one manalens_test_event (build \"editor\") straight to the collector. No install and no session."), GUILayout.Width(160)))
                    {
                        if (playing) SendInPlayMode();
                        else SendFromEditor(settings);
                    }
                }
                if (EditorApplication.isPlaying)
                {
                    using (new EditorGUI.DisabledScope(!Manalens.IsTracking))
                        if (GUILayout.Button("Flush now", GUILayout.Width(100))) Manalens.Flush();
                }
            }
            if (_testResult != "") EditorGUILayout.HelpBox(_testResult, _testResult.StartsWith("Received") ? MessageType.Info : MessageType.Warning);

            if (EditorApplication.isPlaying) PlayModeState();
            Links();
        }

        static ManalensSettings CreateAsset()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
            var settings = ScriptableObject.CreateInstance<ManalensSettings>();
            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }

        static void PlayModeState()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Play mode", EditorStyles.boldLabel);
            if (!Manalens.IsInitialized)
            {
                EditorGUILayout.LabelField("The SDK has not started.", EditorStyles.wordWrappedLabel);
                return;
            }
            Row("Tracking", Manalens.IsTracking ? "On" : "Off (Enabled is false, or consent was not given)");
            Row("Anonymous ID", Manalens.AnonymousId);
            Row("Session ID", Manalens.SessionId);
            Row("Queue", Manalens.QueueSize + " events" + (Manalens.IsSending ? ", sending" : ""));
            Row("Last send", Manalens.LastSendResult == "" ? "none yet" : Manalens.LastSendResult);
            var events = Manalens.RecentEvents;
            _showEvents = EditorGUILayout.Foldout(_showEvents, "Recent events (" + events.Length + ")", true);
            if (!_showEvents) return;
            _eventsScroll = EditorGUILayout.BeginScrollView(_eventsScroll, GUILayout.MaxHeight(240));
            for (var i = events.Length - 1; i >= 0; i--) EditorGUILayout.SelectableLabel(events[i], EditorStyles.wordWrappedMiniLabel, GUILayout.MinHeight(48));
            EditorGUILayout.EndScrollView();
        }

        static void Row(string label, string value)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(label);
                EditorGUILayout.SelectableLabel(value, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
        }

        static void Links()
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (EditorGUILayout.LinkButton("Open Manalens")) Application.OpenURL(AppUrl);
                if (EditorGUILayout.LinkButton("Docs")) Application.OpenURL(DocsUrl);
                GUILayout.FlexibleSpace();
            }
        }

        static void SendInPlayMode()
        {
            Manalens.Track(TestEvent.Name, new Dictionary<string, object> { ["source"] = "unity_editor" });
            Manalens.Flush();
            _testResult = "Tracked " + TestEvent.Name + " and sent the queue. See Last send below.";
        }

        /// <summary>Sends the test event from the editor. Also called by batch-mode checks (-executeMethod).</summary>
        public static void SendFromEditor(ManalensSettings settings)
        {
            var body = TestEvent.Body(Platforms.FromRuntimePlatform(Application.platform.ToString()), TestAnonymousId(),
                string.IsNullOrWhiteSpace(settings.AppVersion) ? Application.version : settings.AppVersion.Trim(),
                (settings.Storefront ?? "").Trim(), System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
            _request = new UnityWebRequest(TestEvent.Url(settings.Endpoint), "POST")
            {
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body)) { contentType = "application/json" },
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 15,
            };
            _request.SetRequestHeader("Content-Type", "application/json");
            _request.SetRequestHeader("x-publishable-key", settings.PublishableKey.Trim());
            _request.SendWebRequest();
            _testResult = "Sending...";
            EditorApplication.update += PollRequest;
        }

        /// <summary>The result of the last test, for batch-mode checks.</summary>
        public static string TestResult => _testResult;
        public static bool TestBusy => _request != null;

        static void PollRequest()
        {
            if (_request == null || !_request.isDone) return;
            EditorApplication.update -= PollRequest;
            var status = (int)_request.responseCode;
            var transportError = _request.result == UnityWebRequest.Result.ConnectionError ? _request.error : null;
            _testResult = TestEvent.Describe(transportError != null ? 0 : status, _request.downloadHandler?.text ?? "", transportError);
            _request.Dispose();
            _request = null;
            _provider?.Repaint();
        }

        /// <summary>The same test player for this project on this computer, so repeated tests count as one player.</summary>
        static string TestAnonymousId()
        {
            var key = "Manalens.TestAnonymousId." + PlayerSettings.productGUID;
            var id = EditorPrefs.GetString(key, "");
            if (id == "")
            {
                id = Ids.NewUuid();
                EditorPrefs.SetString(key, id);
            }
            return id;
        }
    }
}
