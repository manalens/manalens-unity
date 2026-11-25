// One "game launch" of the Unity SDK in a real Unity player or in Play mode,
// driven by scripts/test-unity-player-local.mjs. It uses only the public
// Manalens API, so it tests the Unity layer (ManalensDriver, UnityWebRequest,
// the log capture, FileStorage or PlayerPrefs, Application.wantsToQuit).
//
// Arguments: -manalens-mode=basic -manalens-endpoint=http://127.0.0.1:3294
// -manalens-key=mana_pk_... -manalens-run=unity-player-1 on the command line,
// or ?mode=...&endpoint=...&key=...&run=... in the WebGL page URL.
// It logs one line: MANALENS_RESULT {"mode":"basic","ok":true,...}
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ManalensSdk;
using UnityEngine;

public sealed class E2ERunner : MonoBehaviour
{
    Dictionary<string, string> _args;
    readonly List<string> _failures = new List<string>();
    readonly List<string> _sdkLines = new List<string>();
    readonly Dictionary<string, object> _result = new Dictionary<string, object>();
    readonly List<string> _flushes = new List<string>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        var args = ReadArgs();
        if (!args.ContainsKey("mode")) return;
        var host = new GameObject("E2ERunner");
        DontDestroyOnLoad(host);
        host.AddComponent<E2ERunner>()._args = args;
    }

    static Dictionary<string, string> ReadArgs()
    {
        var args = new Dictionary<string, string>();
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (!arg.StartsWith("-manalens-", StringComparison.Ordinal)) continue;
            var pair = arg.Substring("-manalens-".Length).Split(new[] { '=' }, 2);
            args[pair[0]] = pair.Length > 1 ? pair[1] : "true";
        }
        var url = Application.absoluteURL ?? "";
        var query = url.IndexOf('?');
        if (query >= 0)
        {
            foreach (var part in url.Substring(query + 1).Split('&'))
            {
                var pair = part.Split(new[] { '=' }, 2);
                if (pair.Length == 2) args[Uri.UnescapeDataString(pair[0])] = Uri.UnescapeDataString(pair[1]);
            }
        }
        return args;
    }

    void Check(bool condition, string message)
    {
        if (!condition) _failures.Add(message);
    }

    ManalensOptions Options(Action<ManalensOptions> change = null)
    {
        var options = new ManalensOptions
        {
            PublishableKey = _args["key"],
            Endpoint = _args["endpoint"],
            Build = "unity-player-test",
            Storefront = "itch",
            Enabled = true,
            FlushIntervalSeconds = 1,
        };
        change?.Invoke(options);
        return options;
    }

    Dictionary<string, object> Props(params object[] pairs)
    {
        var props = new Dictionary<string, object> { ["run_id"] = _args["run"] };
        for (var i = 0; i + 1 < pairs.Length; i += 2) props[(string)pairs[i]] = pairs[i + 1];
        return props;
    }

    string DataPath(string name) => Path.Combine(Application.persistentDataPath, name + ".txt");

    /// <summary>Frames until the queue is sent or the time is up.</summary>
    IEnumerator WaitSent(float seconds)
    {
        var until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until && (Manalens.QueueSize > 0 || Manalens.IsSending)) yield return null;
    }

    IEnumerator Wait(float seconds)
    {
        var until = Time.realtimeSinceStartup + seconds;
        while (Time.realtimeSinceStartup < until) yield return null;
    }

    void OnLog(string condition, string stackTrace, LogType type)
    {
        if (condition.StartsWith("Manalens:", StringComparison.Ordinal))
            lock (_sdkLines) _sdkLines.Add(type + " " + condition);
    }

    IEnumerator Start()
    {
        Application.logMessageReceivedThreaded += OnLog;
        Manalens.Flushed += (sent, ok) => _flushes.Add(sent + ":" + ok);
        var mode = _args["mode"];
        _result["mode"] = mode;
        _result["platform"] = Application.platform.ToString();
        _result["is_editor"] = Application.isEditor;
        _result["data_path"] = Application.persistentDataPath;
        var quit = true;
        switch (mode)
        {
            case "editor-default":
                // The README setup: Enabled = !Application.isEditor. Play mode sends nothing and writes nothing.
                Check(Manalens.Init(Options(o => o.Enabled = !Application.isEditor)), "init");
                Manalens.Track("never_sent", Props());
                Debug.LogError("E2E error in the editor, never sent");
                yield return Wait(2);
                Check(Manalens.QueueSize == 0, "disabled: empty queue");
                Check(!File.Exists(DataPath(AnalyticsClient.StateName)), "disabled: no state file");
                break;

            case "editor-on":
                // A test key in Play mode: events are sent; leaving Play mode keeps session_end on disk.
                Check(Manalens.Init(Options()), "init");
                Manalens.Track("level_start", Props("level", 1));
                yield return WaitSent(5);
                Check(Manalens.QueueSize == 0, "editor: queue sent");
                Check(_flushes.Exists(f => f.EndsWith(":True")), "editor: Flushed(ok) event");
                _result["anonymous_id"] = Manalens.AnonymousId;
                break;

            case "basic":
                yield return Basic();
                break;

            case "offline":
                // The collector does not answer: the quit waits QuitTimeoutSeconds, then the queue stays on disk.
                Check(Manalens.Init(Options(o => o.Endpoint = _args["dead-endpoint"])), "init");
                Manalens.Track("offline_event", Props("n", 1));
                yield return Wait(1.5f);
                Check(Manalens.QueueSize >= 3, $"offline: queue holds session_start, game_open, offline_event (has {Manalens.QueueSize})");
                _result["anonymous_id"] = Manalens.AnonymousId;
                _result["quit_started"] = Time.realtimeSinceStartup;
                break;

            case "online":
                // Next launch of the "offline" device: the queue from disk is sent.
                Check(Manalens.Init(Options()), "init");
                Manalens.Track("online_event", Props());
                yield return WaitSent(5);
                Check(Manalens.QueueSize == 0, "online: queue sent");
                _result["anonymous_id"] = Manalens.AnonymousId;
                break;

            case "crash":
                // The script kills the process after MANALENS_READY: no quit, no OnQuit().
                Check(Manalens.Init(Options(o => o.Build = "unity-crash")), "init");
                Manalens.Track("before_crash", Props());
                yield return WaitSent(5);
                yield return Wait(2.5f); // Update ticks save the session's last activity.
                _result["anonymous_id"] = Manalens.AnonymousId;
                _result["crashed_session"] = Manalens.SessionId;
                Report();
                Debug.Log("MANALENS_READY");
                yield return Wait(60);
                yield break;

            case "recovery":
                Check(Manalens.Init(Options()), "init");
                yield return WaitSent(5);
                _result["anonymous_id"] = Manalens.AnonymousId;
                _result["session"] = Manalens.SessionId;
                break;

            case "consent":
            {
                Check(Manalens.Init(Options(o => o.ConsentRequired = true)), "init");
                Manalens.Track("before_consent", Props());
                yield return Wait(1.5f);
                Check(!File.Exists(DataPath(AnalyticsClient.QueueName)), "consent: no queue file before consent");
                Manalens.SetConsent(true);
                Manalens.Track("after_consent", Props());
                yield return WaitSent(5);
                _result["anonymous_id"] = Manalens.AnonymousId;
                Manalens.SetConsent(false);
                Manalens.Track("after_withdraw", Props());
                yield return Wait(1.5f);
                Check(File.Exists(DataPath(AnalyticsClient.OptOutName)), "consent: SetConsent(false) writes the opt-out marker");
                Check(!File.Exists(DataPath(AnalyticsClient.QueueName)), "consent: SetConsent(false) deletes the queue");
                Check(File.Exists(DataPath(AnalyticsClient.StateName)), "consent: state file kept");
                break;
            }

            case "damaged":
            {
                // Same device as "consent": cut the state file in half and remove the marker.
                var statePath = DataPath(AnalyticsClient.StateName);
                var text = File.Exists(statePath) ? File.ReadAllText(statePath) : "";
                Check(text.Length > 0, "damaged: a state file from the consent launch");
                File.WriteAllText(statePath, text.Substring(0, text.Length / 2));
                File.Delete(DataPath(AnalyticsClient.OptOutName));
                Check(Manalens.Init(Options()), "init");
                Manalens.Track("after_damage", Props());
                yield return Wait(2);
                Check(_flushes.Count == 0, "damaged: nothing sent");
                Check(_sdkLines.Exists(l => l.Contains("damaged")), "damaged: the SDK logs that the state is damaged");
                Check(File.ReadAllText(statePath) == text.Substring(0, text.Length / 2), "damaged: the state is not overwritten");
                break;
            }

            case "webgl":
                // A browser page: PlayerPrefs storage (IndexedDB), and CORS on the real API.
                Check(Manalens.Init(Options()), "init");
                Manalens.Track("webgl_event", Props());
                Debug.LogError("E2E WebGL error");
                yield return WaitSent(8);
                Check(PlayerPrefs.HasKey(AnalyticsClient.StateName), "webgl: state in PlayerPrefs");
                _result["anonymous_id"] = Manalens.AnonymousId;
                _result["queue_size"] = Manalens.QueueSize.ToString(CultureInfo.InvariantCulture);
                quit = false;
                break;

            case "disabled":
                Check(Manalens.Init(Options(o => o.Enabled = false)), "init");
                Manalens.Track("never_sent", Props());
                yield return Wait(1.5f);
                Check(_flushes.Count == 0, "disabled: nothing sent");
                Check(!File.Exists(DataPath(AnalyticsClient.StateName)), "disabled: no state file");
                break;

            default:
                _failures.Add("unknown mode " + mode);
                break;
        }
        Report();
        if (quit) Quit();
    }

    IEnumerator Basic()
    {
        Check(Manalens.Init(Options(o => o.MaxAutoErrorsPerSession = 5)), "init");
        Check(!Manalens.Init(Options()), "a second Init() is refused");
        Manalens.Identify("player-" + _args["run"], new Dictionary<string, object> { ["plan"] = "supporter" });
        Manalens.Track("level_complete", Props("level", 2, "score", 1200, "ratio", 0.75, "perfect", true, "nested", new Dictionary<string, object> { ["a"] = 1 }));
        Manalens.TrackError("Save failed for slot 3", new TrackErrorOptions { Severity = "critical", ErrorType = "SaveFailed", Properties = Props() });
        yield return null;
        // Captured from the Unity log: an error, a logged exception, an uncaught exception.
        // (Debug.LogAssertion is compiled out of release players: [Conditional("UNITY_ASSERTIONS")].)
        Debug.LogError("E2E boss fight failed to load");
        Debug.LogException(new InvalidOperationException("E2E logged exception"));
        Debug.LogWarning("E2E warning, not captured");
        Debug.LogError("Manalens: E2E line with the SDK prefix, never captured");
        _throwInUpdate = true;
        yield return null;
        yield return null;
        // From another thread: the capture only copies the message.
        var thread = new System.Threading.Thread(() => Debug.LogError("E2E error from a worker thread"));
        thread.Start();
        thread.Join();
        // Over the per-session cap of 5 automatic errors: 3 more distinct errors are dropped.
        for (var i = 0; i < 3; i++) Debug.LogError("E2E over the cap " + i);
        yield return WaitSent(5);
        Check(Manalens.QueueSize == 0, $"basic: queue sent (left {Manalens.QueueSize})");
        Check(_flushes.Exists(f => f.EndsWith(":True")), "basic: Flushed(ok) event");
        Check(!_sdkLines.Exists(l => l.StartsWith("Error", StringComparison.Ordinal) && !l.Contains("E2E line")), "basic: no SDK error lines");
        _result["anonymous_id"] = Manalens.AnonymousId;
        _result["session"] = Manalens.SessionId;
        _result["link_url"] = Manalens.LinkUrl("https://example.com/wiki");
    }

    bool _throwInUpdate;

    void Update()
    {
        if (!_throwInUpdate) return;
        _throwInUpdate = false;
        throw new ArgumentException("E2E uncaught exception in Update");
    }

    void Report()
    {
        _result["flushes"] = _flushes;
        lock (_sdkLines) _result["sdk_lines"] = new List<string>(_sdkLines);
        _result["failures"] = _failures;
        _result["ok"] = _failures.Count == 0;
        Debug.Log("MANALENS_RESULT " + ToJson(_result));
    }

    void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.ExitPlaymode();
#else
        // Desktop players: the SDK delays this quit (Application.wantsToQuit) to send session_end.
        // Application.quitting comes after that delay: the two lines measure it.
        var calledAt = Time.realtimeSinceStartup;
        Application.quitting += () => Debug.Log("MANALENS_QUIT_DELAY_MS " + ((int)((Time.realtimeSinceStartup - calledAt) * 1000)).ToString(CultureInfo.InvariantCulture));
        Application.Quit();
#endif
    }

    static string ToJson(object value)
    {
        switch (value)
        {
            case null: return "null";
            case bool b: return b ? "true" : "false";
            case string s:
                var sb = new StringBuilder("\"");
                foreach (var c in s)
                {
                    if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                    else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                }
                return sb.Append('"').ToString();
            case float f: return f.ToString("R", CultureInfo.InvariantCulture);
            case double d: return d.ToString("R", CultureInfo.InvariantCulture);
            case int i: return i.ToString(CultureInfo.InvariantCulture);
            case IDictionary<string, object> map:
                var parts = new List<string>();
                foreach (var pair in map) parts.Add(ToJson(pair.Key) + ":" + ToJson(pair.Value));
                return "{" + string.Join(",", parts) + "}";
            case IEnumerable<string> list:
                var items = new List<string>();
                foreach (var item in list) items.Add(ToJson(item));
                return "[" + string.Join(",", items) + "]";
            default: return ToJson(value.ToString());
        }
    }
}
