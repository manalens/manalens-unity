using System;
using System.Collections.Generic;

namespace ManalensSdk
{
    /// <summary>Options for Manalens.Init(). PublishableKey and Endpoint are required.</summary>
    public sealed class ManalensOptions
    {
        /// <summary>The project publishable key (mana_pk_...). Never a secret key or an API key.</summary>
        public string PublishableKey = "";
        /// <summary>The collector URL, without /v0/events.</summary>
        public string Endpoint = "";
        /// <summary>
        /// The API URL of LinkSteam(). Empty (default): the Endpoint with an
        /// events. host changed to api. (https://events.manalens.dev gives
        /// https://api.manalens.dev); any other endpoint is used as it is.
        /// </summary>
        public string ApiEndpoint = "";
        /// <summary>
        /// false: the SDK sends nothing, writes no files and keeps no ID.
        /// Use <c>Enabled = !Application.isEditor</c> to keep editor runs out of your reports.
        /// </summary>
        public bool Enabled = true;
        /// <summary>Up to 32 characters. Null: Application.version.</summary>
        public string AppVersion;
        /// <summary>Up to 64 characters, for example a build number.</summary>
        public string Build = "";
        /// <summary>steam, itch, epic, gog, app_store, play, psn, xbox, nintendo or other.</summary>
        public string Storefront = "";
        /// <summary>
        /// Empty (default): detected from Application.platform. Set it when the
        /// detection cannot name the console, for example "switch2" in a
        /// Nintendo Switch 2 build. Values: windows, macos, linux, web, android,
        /// ios, ps5, xbox_series, xbox_one, switch, switch2 or other.
        /// </summary>
        public string Platform = "";
        /// <summary>
        /// true: verify the Steam account once per launch, after consent
        /// (P-2815). Unity: needs Steamworks.NET (the define
        /// MANALENS_STEAMWORKS_NET) and a started Steam API; the SDK asks for a
        /// Web API ticket with the identity "manalens" and sends it, 20 seconds
        /// after the first accepted event batch of the launch (Manalens merges
        /// installs only when the install has events before a link). Without
        /// Steamworks.NET it does nothing. Off by default.
        /// </summary>
        public bool SteamLink;
        /// <summary>true: no events and no persistent ID until SetConsent(true).</summary>
        public bool ConsentRequired;
        public double FlushIntervalSeconds = 10;
        /// <summary>Send when this many events are queued; also the batch size (at most 50).</summary>
        public int FlushAt = 20;
        /// <summary>Most events kept on disk. The oldest are dropped first.</summary>
        public int MaxQueue = 1000;
        /// <summary>true: in a desktop player, delay the quit to send session_end and flush.</summary>
        public bool HandleQuit = true;
        public double QuitTimeoutSeconds = 2;
        /// <summary>Send exceptions, Debug.LogError and failed asserts as error events.</summary>
        public bool CaptureErrors = true;
        /// <summary>Also send Debug.LogWarning messages (off: they are often noisy).</summary>
        public bool CaptureWarnings;
        /// <summary>Automatic errors in one session; TrackError() calls do not count.</summary>
        public int MaxAutoErrorsPerSession = 50;
        /// <summary>
        /// Desktop players (Windows, macOS, Linux): at the next launch, add the
        /// crash data that Unity wrote (the crash folder's error.log, or the
        /// previous Player log) to app_crash: stack, error_type, message,
        /// fingerprint, crash_source and symbolicated. No crash handler.
        /// </summary>
        public bool CaptureCrashStacks = true;
        /// <summary>
        /// Hang detection (P-2833): a watchdog thread sends an error with
        /// error_type app_hang when one frame takes longer than this many
        /// seconds (the main thread is stuck). 0 turns it off; other values are
        /// clamped to 1 to 60 s. Not counted while
        /// the game is unfocused or paused, while the editor is paused, while a
        /// debugger is attached, in the first frames after Init(), on WebGL, and
        /// on a quiet server build.
        /// </summary>
        public double HangThresholdSeconds = 5;
        /// <summary>
        /// Gets a copy of each event before it is queued: track events (also
        /// error and the automatic events such as session_start), identify and traits.
        /// Keys: type, event, properties, traits, user_id, message_id,
        /// anonymous_id, session_id, context, ... Return the event to send
        /// (changed or not), or null to drop it. The SDK takes event, properties,
        /// traits and user_id from the result and keeps the other fields. When
        /// the result changes or removes type, message_id, anonymous_id,
        /// session_id or context.app, the original event is sent. Errors pass
        /// the error limits first. If it throws, the event is dropped. Do not
        /// record events inside it: they are dropped.
        /// </summary>
        public Func<Dictionary<string, object>, Dictionary<string, object>> BeforeSend;
        /// <summary>
        /// A dedicated server build (UNITY_SERVER, or a player in batch mode) is
        /// not a player. false (default): on a server build the SDK sends no
        /// game_open, session_start, session_end or app_crash, so the
        /// server does not count as an install or a session. Track() and
        /// Identify() still work. true: a server build sends them as a client does.
        /// </summary>
        public bool TrackServerBuilds;
        /// <summary>Log each queued event and each failed send.</summary>
        public bool Debug;
    }

    /// <summary>Optional details for TrackError().</summary>
    public sealed class TrackErrorOptions
    {
        /// <summary>debug, info, warning, error (default) or critical.</summary>
        public string Severity = "error";
        /// <summary>The error class, for example SaveFailed.</summary>
        public string ErrorType;
        /// <summary>false when the game could not recover.</summary>
        public bool Handled = true;
        /// <summary>A stack trace text (cut to 1024 characters).</summary>
        public string Stack;
        public string File;
        public int Line;
        public string Function;
        /// <summary>Your own grouping key. Default: a hash of the error type and the place in the code, or of the message without numbers.</summary>
        public string Fingerprint;
        /// <summary>More scalar properties (level, mode, ...).</summary>
        public IDictionary<string, object> Properties;
    }
}
