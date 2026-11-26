using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;
using UnityEngine.Scripting;

namespace ManalensSdk
{
    /// <summary>
    /// Manalens for Unity. Sends game events to POST {endpoint}/v0/events
    /// with the project publishable key, the same envelope as the web and Godot
    /// SDKs, and context.app in place of context.page.
    ///
    ///   Manalens.Init(new ManalensOptions {
    ///       PublishableKey = "mana_pk_...",
    ///       Endpoint = "https://collector.example.com",
    ///       Build = "571", Storefront = "steam",
    ///       Enabled = !Application.isEditor,
    ///   });
    ///   Manalens.Track("level_complete", new Dictionary&lt;string, object&gt; { ["level"] = 3 });
    /// </summary>
    public static class Manalens
    {
        static AnalyticsClient _client;
        static ManalensDriver _driver;
        static int _mainThreadId = -1;
        static readonly ILog Log = new UnityLog();

        /// <summary>After each send: the number of events sent (0 when it failed) and whether it succeeded.</summary>
        public static event Action<int, bool> Flushed;

        /// <summary>
        /// Events were dropped and will never be sent. status: the HTTP status when
        /// the collector refused a batch (a 4xx other than 408 and 429), or 0 when
        /// the SDK dropped events itself (over MaxQueue, or older than 7 days).
        /// </summary>
        public static event Action<int, int> SendFailed;

        /// <summary>Resets the static state when domain reload is off ("Enter Play Mode Options").</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        [Preserve]
        static void ResetStatics()
        {
            _client = null;
            _driver = null;
            _mainThreadId = -1;
            Flushed = null;
            SendFailed = null;
        }

        /// <summary>
        /// Project Settings > Manalens: starts the SDK after the first scene
        /// loads (after Awake, before Start) when the settings asset has a key
        /// and AutoInitialize. A game that called Init() in Awake keeps its own
        /// options.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        [Preserve]
        static void AutoInitialize()
        {
            if (_client != null) return;
            var settings = ManalensSettings.Load();
            if (settings == null || !settings.AutoInitialize || string.IsNullOrWhiteSpace(settings.PublishableKey)) return;
            if (settings.Debug) Log.Info("starting from Project Settings > Manalens");
            Init(settings.ToOptions());
        }

        /// <summary>Starts the SDK with the options of Project Settings > Manalens. Returns false when there is no settings asset or Init fails.</summary>
        public static bool Init()
        {
            var settings = ManalensSettings.Load();
            if (settings == null)
            {
                Log.Error("no Project Settings > Manalens asset (Assets/Resources/ManalensSettings.asset); pass ManalensOptions to Init");
                return false;
            }
            return Init(settings.ToOptions());
        }

        /// <summary>
        /// Starts the SDK once, on the main thread, for example in the Awake() of
        /// your first scene. Returns false (and logs why) when the options are
        /// wrong, for example a missing endpoint or a secret key.
        /// </summary>
        public static bool Init(ManalensOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (_client != null)
            {
                Log.Warning("Init() was already called");
                return false;
            }
            var environment = new AppEnvironment
            {
                Platform = Platforms.FromRuntimePlatform(Application.platform.ToString()),
                Locale = CurrentLocale(),
                UtcOffsetMinutes = CurrentUtcOffsetMinutes(),
                DefaultAppVersion = Application.version,
            };
#if UNITY_WEBGL && !UNITY_EDITOR
            IStorage storage = BrowserStorage.Create();
#else
            IStorage storage = new FileStorage(Application.persistentDataPath, Log);
#endif
            var client = new AnalyticsClient(options, environment, storage, new UnityWebRequestTransport(), new SystemClock(), Log);
            client.Flushed += (sent, ok) => Flushed?.Invoke(sent, ok);
            client.SendFailed += (status, dropped) => SendFailed?.Invoke(status, dropped);
            if (!client.Init()) return false;
            _client = client;
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            if (client.IsDisabled) return true;
            var host = new GameObject("Manalens") { hideFlags = HideFlags.HideInHierarchy };
            UnityEngine.Object.DontDestroyOnLoad(host);
            _driver = host.AddComponent<ManalensDriver>();
            _driver.Client = client;
            return true;
        }

        /// <summary>Records a named event with scalar properties (string, number, bool, null; enums as their name).</summary>
        public static void Track(string eventName, IDictionary<string, object> properties = null)
        {
            if (Ready()) _client.Track(eventName, properties);
        }

        /// <summary>Records an error as an error event. See TrackErrorOptions.</summary>
        public static void TrackError(string message, TrackErrorOptions options = null)
        {
            if (Ready()) _client.TrackError(message, options);
        }

        /// <summary>Records a caught exception as an error event (type, message, stack and place in the code).</summary>
        public static void TrackError(Exception exception, TrackErrorOptions options = null)
        {
            if (Ready()) _client.TrackError(exception, options);
        }

        /// <summary>Attaches a known player ID (1 to 255 characters) and traits to this device.</summary>
        public static void Identify(string userId, IDictionary<string, object> traits = null)
        {
            if (Ready()) _client.Identify(userId, traits);
        }

        /// <summary>Forgets the player (sign out): new anonymous ID and new session.</summary>
        public static void Reset()
        {
            _client?.Reset();
        }

        /// <summary>true: start tracking (sends game_open). false: delete the ID, the session and the queue.</summary>
        public static void SetConsent(bool granted)
        {
            if (Ready()) _client.SetConsent(granted);
        }

        /// <summary>Replaces the BeforeSend option for error events. Null removes it.</summary>
        public static void SetBeforeSend(Func<Dictionary<string, object>, Dictionary<string, object>> callback)
        {
            _client?.SetBeforeSend(callback);
        }

        /// <summary>Sends queued events now (on the main thread), or at the next frame from another thread.</summary>
        public static void Flush()
        {
            if (_client == null) return;
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId) _client.SendNow();
            else _client.RequestFlush();
        }

        /// <summary>
        /// Ends the session and sends the queue for at most QuitTimeoutSeconds.
        /// Use it before your own quit when HandleQuit is false:
        /// <c>yield return Manalens.Shutdown(); Application.Quit();</c>
        /// Unsent events stay on disk for the next launch.
        /// </summary>
        public static IEnumerator Shutdown()
        {
            return _driver != null ? _driver.ShutdownRoutine() : Empty();
        }

        /// <summary>Changes the collector URL (for example after a config download).</summary>
        public static void SetEndpoint(string endpoint)
        {
            _client?.SetEndpoint(endpoint);
        }

        /// <summary>
        /// Adds this install's handoff parameter to a website URL before the game
        /// opens it, so the website joins the browser to this player:
        /// <c>Application.OpenURL(Manalens.LinkUrl("https://example.com/wiki"));</c>
        /// Before consent (or before Init) the URL is returned unchanged.
        /// </summary>
        public static string LinkUrl(string url) => _client != null ? _client.LinkUrl(url) : url;

        public static string AnonymousId => _client?.AnonymousId ?? "";
        public static string SessionId => _client?.SessionId ?? "";
        public static int QueueSize => _client?.QueueSize ?? 0;
        public static bool IsSending => _client?.IsSending ?? false;
        public static bool IsInitialized => _client != null;
        /// <summary>false before Init, when Enabled is false, and before consent in consent mode.</summary>
        public static bool IsTracking => _client != null && _client.IsActive;
        /// <summary>The result of the last send, for example "HTTP 202: 12 events stored".</summary>
        public static string LastSendResult => _client?.LastSendResult ?? "";
        /// <summary>The JSON of the last queued events, oldest first (Project Settings > Manalens in Play mode).</summary>
        public static string[] RecentEvents => _client?.RecentEvents ?? Array.Empty<string>();

        static bool Ready()
        {
            if (_client != null) return true;
            Log.Warning("call Init() first");
            return false;
        }

        static IEnumerator Empty()
        {
            yield break;
        }

        /// <summary>The system locale, for example en-US. IL2CPP builds often have only the invariant culture: then the system language.</summary>
        static int? CurrentUtcOffsetMinutes()
        {
            try { return (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMinutes); }
            catch (Exception) { return null; }
        }

        static string CurrentLocale()
        {
            try
            {
                var name = CultureInfo.CurrentCulture.Name;
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch (Exception)
            {
                // Some platforms have no culture data.
            }
            return LanguageCode(Application.systemLanguage);
        }

        static string LanguageCode(SystemLanguage language)
        {
            switch (language)
            {
                case SystemLanguage.Arabic: return "ar";
                case SystemLanguage.Chinese: return "zh";
                case SystemLanguage.ChineseSimplified: return "zh-Hans";
                case SystemLanguage.ChineseTraditional: return "zh-Hant";
                case SystemLanguage.Czech: return "cs";
                case SystemLanguage.Danish: return "da";
                case SystemLanguage.Dutch: return "nl";
                case SystemLanguage.English: return "en";
                case SystemLanguage.Finnish: return "fi";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.German: return "de";
                case SystemLanguage.Greek: return "el";
                case SystemLanguage.Hebrew: return "he";
                case SystemLanguage.Hungarian: return "hu";
                case SystemLanguage.Indonesian: return "id";
                case SystemLanguage.Italian: return "it";
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.Norwegian: return "no";
                case SystemLanguage.Polish: return "pl";
                case SystemLanguage.Portuguese: return "pt";
                case SystemLanguage.Romanian: return "ro";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.Swedish: return "sv";
                case SystemLanguage.Thai: return "th";
                case SystemLanguage.Turkish: return "tr";
                case SystemLanguage.Ukrainian: return "uk";
                case SystemLanguage.Vietnamese: return "vi";
                default: return null;
            }
        }
    }
}
