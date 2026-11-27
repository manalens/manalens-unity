using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ManalensSdk
{
    /// <summary>
    /// The engine-independent SDK: envelopes, identity, sessions, the offline
    /// queue, batching, retries, consent and error events. It has no
    /// UnityEngine reference. The engine layer gives it storage, a transport,
    /// a clock and a log, calls Update() every frame and reports focus and quit.
    ///
    /// Thread safety: every public method takes one lock, so Track() may be
    /// called from any thread. The transport is used only inside Update(),
    /// SendNow(), OnFocusChanged() and BeginShutdown(): call those on the
    /// thread the transport needs (Unity: the main thread).
    /// </summary>
    public sealed class AnalyticsClient
    {
        public const string LibraryName = "Manalens Unity SDK";
        public const string LibraryVersion = "0.3.0";
        public const string Engine = "unity";
        public const string StateName = "manalens_state";
        public const string QueueName = "manalens_queue";
        /// <summary>
        /// Exists while the player has withdrawn consent. Checked apart from the
        /// state, so a lost or broken state file cannot undo an opt-out.
        /// </summary>
        public const string OptOutName = "manalens_optout";
        /// <summary>A copy of a state file that could not be parsed.</summary>
        public const string CorruptStateName = "manalens_state_corrupt";
        public const double SessionTimeoutSeconds = 30 * 60;
        /// <summary>A session is never older than this. A game that stays open gets a session_end and a session_start every 24 hours.</summary>
        public const double SessionMaxAgeSeconds = 24 * 60 * 60;
        /// <summary>The collector accepts app events up to 7 days old. Keep a margin for clock skew.</summary>
        public const double MaxEventAgeSeconds = 7 * 24 * 3600 - 3600;
        public const double MaxBackoffSeconds = 300;
        /// <summary>The longest wait that a Retry-After header can ask for (1 hour).</summary>
        public const double MaxRetryAfterSeconds = 3600;
        /// <summary>Identity handoff: the URL parameter that the web SDK reads.</summary>
        public const string HandoffParam = "mana_hid";
        /// <summary>The values of ManalensOptions.Platform (the collector platforms).</summary>
        public static readonly string[] PlatformValues = { "windows", "macos", "linux", "web", "android", "ios", "ps5", "xbox_series", "xbox_one", "switch", "switch2", "other" };
        public static readonly string[] Storefronts = { "steam", "itch", "epic", "gog", "app_store", "play", "psn", "xbox", "nintendo", "other" };
        public static readonly string[] Severities = { "debug", "info", "warning", "error", "critical" };
        /// <summary>The <c>source</c> of an error: a TrackError call, or an automatic capture.</summary>
        public const string SourceManual = "manual";
        public const string SourceAuto = "auto";
        /// <summary>Error limits (as the Godot SDK and Sentry): one fingerprint at most once a second, at most 20 errors in 10 seconds.</summary>
        public const long ErrorDedupeMs = 1000;
        public const long ErrorWindowMs = 10000;
        public const int ErrorWindowMax = 20;
        /// <summary>Most automatic errors sent in one Update().</summary>
        public const int AutoErrorsPerUpdate = 5;
        public const int MaxPendingLogs = 100;
        public const int BreadcrumbCount = 20;
        /// <summary>The collector keeps string properties up to 1024 characters.</summary>
        public const int MaxString = 1024;
        /// <summary>The collector refuses properties or traits with more keys (P-2700).</summary>
        public const int MaxPropertyKeys = 50;
        /// <summary>The collector refuses properties or traits whose JSON has more UTF-8 bytes (P-2700).</summary>
        public const int MaxPropertyBytes = 8 * 1024;
        /// <summary>
        /// Protected traits (P-2814): only a server with the secret key may set
        /// them. The collector drops them from game events, so Traits() does not send them.
        /// </summary>
        public static readonly string[] ProtectedTraits = { "email", "itch_user_id", "stripe_customer_id", "paddle_customer_id" };
        /// <summary>LinkSteam(): the identity of the Steam Web API ticket. The server accepts only this one.</summary>
        public const string SteamTicketIdentity = "manalens";
        /// <summary>LinkSteam() waits for a 429 Retry-After of at most this many seconds, once.</summary>
        public const double LinkMaxRetryAfterSeconds = 60;
        const string LogPrefix = "Manalens:";
        static readonly Regex LabelRe = new Regex("^[A-Za-z0-9._+\\- ]+$", RegexOptions.CultureInvariant);

        /// <summary>After each send: the number of events sent (0 when it failed) and whether it succeeded.</summary>
        public event Action<int, bool> Flushed;
        /// <summary>
        /// Events were dropped and will never be sent. status: the HTTP status
        /// when the collector refused a batch (a 4xx other than 408 and 429), or
        /// 0 when the SDK dropped events itself (over MaxQueue, or older than 7 days).
        /// </summary>
        public event Action<int, int> SendFailed;
        /// <summary>
        /// The answer to LinkSteam() (P-2815): linked, the HTTP status (0 when no
        /// request was made or the network failed), and the API error code
        /// (for example STEAM_TICKET_INVALID or STEAM_NOT_CONNECTED) or an SDK
        /// code: NETWORK_ERROR, CONSENT_REQUIRED or IDENTITY_CHANGED. "" on success.
        /// </summary>
        public event Action<bool, int, string> SteamLinked;

        readonly object _gate = new object();
        readonly object _logGate = new object();
        readonly ManalensOptions _options;
        readonly AppEnvironment _env;
        readonly IStorage _storage;
        readonly ITransport _transport;
        readonly IClock _clock;
        readonly ILog _log;
        /// <summary>A number in [0, 1) for the backoff jitter. Tests replace it.</summary>
        public Func<double> Random = new Random().NextDouble;
        /// <summary>Makes message, session and anonymous IDs. Tests replace it.</summary>
        public Func<string> NewId = Ids.NewUuid;

        bool _initialized, _disabled, _identityActive, _focused = true;
        /// <summary>A server build without TrackServerBuilds: no automatic client events.</summary>
        bool _serverQuiet;
        StateFile _state = new StateFile();
        bool? _pendingConsent;
        // Fail closed: the state file exists but could not be read, or could not
        // be parsed; an opt-out marker exists; consent was withdrawn in this run;
        // an opt-out write failed and is retried.
        bool _stateUnreadable, _stateCorrupt, _optOutMarker, _withdrawnThisRun, _optOutWritePending;
        string _anonymousId = "", _userId = "", _sessionId = "";
        double _sessionStartedAt, _lastActivityAt, _nextTickAt, _backoffUntil;
        int _flushAt = 20, _maxQueue = 1000, _failures;
        string _appVersion = "", _build = "", _storefront = "", _platform = "other", _url = "";
        readonly List<QueueEntry> _queue = new List<QueueEntry>();
        bool _queueDirty, _sending, _flushRequested;
        string[] _deferredIdentify;
        Dictionary<string, object> _deferredTraits;
        /// <summary>Traits() before consent, merged.</summary>
        Dictionary<string, object> _heldTraits;
        bool _warnedProtectedTraits;
        string _linkUrl = "";
        /// <summary>A Steam ticket (hex) that waits for consent or for Update(), or null.</summary>
        string _pendingLinkTicket;
        /// <summary>The body of the link request in flight or waiting for its retry, and the anonymous ID it carries.</summary>
        string _linkBody, _linkAnonymousId;
        bool _linkInFlight;
        int _linkAttempt;
        double _linkRetryAt = -1;
        Func<Dictionary<string, object>, Dictionary<string, object>> _beforeSend;
        bool _inBeforeSend;
        readonly List<string> _breadcrumbs = new List<string>();
        /// <summary>The JSON of the last queued events, oldest first (Project Settings > Manalens in Play mode).</summary>
        readonly List<string> _recent = new List<string>();
        public const int RecentCount = 20;
        string _lastSendResult = "";
        readonly Dictionary<string, long> _errorSeen = new Dictionary<string, long>();
        readonly List<long> _errorTimes = new List<long>();
        int _autoErrors, _errorsDropped;
        List<PendingLog> _pendingLogs = new List<PendingLog>();
        readonly List<string> _homeDirs = ErrorTools.HomeDirs();
        int _logOverflow;

        sealed class QueueEntry
        {
            public double At;
            public string MessageId;
            public string Json;
        }

        sealed class PendingLog
        {
            public string Message;
            public string Stack;
            public LogKind Kind;
        }

        public AnalyticsClient(ManalensOptions options, AppEnvironment environment, IStorage storage, ITransport transport, IClock clock = null, ILog log = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _env = environment ?? new AppEnvironment();
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _clock = clock ?? new SystemClock();
            _log = log ?? new NullLog();
            _beforeSend = options.BeforeSend;
        }

        // Public API ---------------------------------------------------------

        /// <summary>
        /// Checks the options and starts the SDK: the persistent identity, a
        /// session, game_open and session_start (unless consent is required and
        /// not given). Returns false when the options are wrong; the SDK then
        /// stays off and logs why.
        /// </summary>
        public bool Init()
        {
            lock (_gate)
            {
                if (_initialized)
                {
                    _log.Warning("Init() was already called");
                    return false;
                }
                if (!_options.Enabled)
                {
                    // Turned off (for example in the editor): every call is a silent no-op.
                    _disabled = true;
                    _initialized = true;
                    if (_options.Debug) _log.Info("disabled (Enabled = false); no events are sent");
                    return true;
                }
                var key = _options.PublishableKey ?? "";
                var endpoint = (_options.Endpoint ?? "").Trim();
                if (key == "" || endpoint == "")
                {
                    _log.Error("PublishableKey and Endpoint are required");
                    return false;
                }
                // Only a publishable key (mana_pk_) may ship in a game build: never a secret key or an API key.
                var refusal = Keys.PublishableKeyRefusal(key);
                if (refusal != null)
                {
                    _log.Error("PublishableKey refused: " + refusal);
                    return false;
                }
                if (!SetUrl(endpoint)) return false;
                _flushAt = Math.Max(1, Math.Min(50, _options.FlushAt));
                _maxQueue = Math.Max(_options.MaxQueue, _flushAt);
                var explicitVersion = _options.AppVersion != null;
                _appVersion = CheckLabel("AppVersion", explicitVersion ? _options.AppVersion : _env.DefaultAppVersion, 32, explicitVersion);
                _build = CheckLabel("Build", _options.Build, 64, true);
                _storefront = _options.Storefront ?? "";
                if (_storefront != "" && Array.IndexOf(Storefronts, _storefront) < 0)
                {
                    _log.Warning("Storefront must be one of " + string.Join(", ", Storefronts) + "; ignored");
                    _storefront = "";
                }
                _platform = _env.Platform ?? "other";
                var platform = _options.Platform ?? "";
                if (platform != "" && Array.IndexOf(PlatformValues, platform) < 0)
                    _log.Warning("Platform must be one of " + string.Join(", ", PlatformValues) + "; detected instead");
                else if (platform != "")
                    _platform = platform;
                _initialized = true;
                _serverQuiet = _env.IsServerBuild && !_options.TrackServerBuilds;
                if (_serverQuiet) _log.Info("server build (UNITY_SERVER or batch mode); no game_open, session or app_crash events. Set TrackServerBuilds to send them");
                LoadState();
                _nextTickAt = Now() + TickInterval();
                if (_pendingConsent.HasValue)
                {
                    var granted = _pendingConsent.Value;
                    _pendingConsent = null;
                    ApplyConsent(granted);
                }
                if (IdentityAllowed() && !_identityActive) StartIdentity();
                return true;
            }
        }

        /// <summary>Records a named event with scalar properties (string, number, bool, null; enums as their name).</summary>
        public void Track(string eventName, IDictionary<string, object> properties = null)
        {
            lock (_gate)
            {
                if (!ReadyToRecord()) return;
                if (string.IsNullOrEmpty(eventName) || eventName.Length > 128)
                {
                    _log.Warning("event name must be 1 to 128 characters");
                    return;
                }
                Touch();
                Enqueue(Build("track", eventName, Scalars(properties), -1));
            }
        }

        /// <summary>Records an error as an error event. See TrackErrorOptions.</summary>
        public void TrackError(string message, TrackErrorOptions options = null)
        {
            lock (_gate) RecordError(message ?? "", options ?? new TrackErrorOptions(), SourceManual);
        }

        /// <summary>Records a caught exception as an error event: its type, message, stack and place in the code.</summary>
        public void TrackError(Exception exception, TrackErrorOptions options = null)
        {
            if (exception == null) return;
            options = options ?? new TrackErrorOptions();
            var frame = ErrorTools.FirstFrame(exception.StackTrace);
            if (string.IsNullOrEmpty(options.ErrorType)) options.ErrorType = exception.GetType().Name;
            if (string.IsNullOrEmpty(options.Stack)) options.Stack = exception.StackTrace;
            if (string.IsNullOrEmpty(options.File) && string.IsNullOrEmpty(options.Function))
            {
                options.File = frame.File;
                options.Line = frame.Line;
                options.Function = frame.Function;
            }
            TrackError(exception.Message, options);
        }

        /// <summary>Replaces the BeforeSend option (it runs on every event). Null removes it.</summary>
        public void SetBeforeSend(Func<Dictionary<string, object>, Dictionary<string, object>> callback)
        {
            lock (_gate) _beforeSend = callback;
        }

        /// <summary>Attaches a known player ID (1 to 255 characters) and traits to this device.</summary>
        public void Identify(string userId, IDictionary<string, object> traits = null)
        {
            lock (_gate)
            {
                if (_disabled) return;
                if (!_initialized)
                {
                    _log.Warning("call Init() first");
                    return;
                }
                if (string.IsNullOrEmpty(userId) || userId.Length > 255)
                {
                    _log.Warning("userId must be 1 to 255 characters");
                    return;
                }
                if (!IdentityAllowed())
                {
                    _deferredIdentify = new[] { userId };
                    _deferredTraits = traits == null ? null : new Dictionary<string, object>(traits);
                    return;
                }
                _userId = userId;
                _state.Set("identity.user_id", userId);
                SaveState();
                Touch();
                Enqueue(Build("identify", null, new Dictionary<string, object>(), -1, Scalars(traits)));
                _flushRequested = true;
            }
        }

        /// <summary>
        /// Sets traits on this player, with or without a user ID (P-2814).
        /// Before Identify() the traits belong to this install's anonymous ID;
        /// after it, to the user. Values: string, number, bool or null (null
        /// clears a trait). At most 50 keys and 8 KB of JSON. Like Identify(), it
        /// waits for consent: calls before consent are merged and sent then.
        /// The protected traits (email, itch_user_id, stripe_customer_id,
        /// paddle_customer_id) are not sent (one warning).
        /// </summary>
        public void Traits(IDictionary<string, object> traits)
        {
            lock (_gate)
            {
                if (_disabled) return;
                if (!_initialized)
                {
                    _log.Warning("call Init() first");
                    return;
                }
                var kept = new Dictionary<string, object>();
                var dropped = new List<string>();
                if (traits != null)
                    foreach (var pair in traits)
                    {
                        if (Array.IndexOf(ProtectedTraits, pair.Key) >= 0) dropped.Add(pair.Key);
                        else kept[pair.Key] = pair.Value;
                    }
                if (dropped.Count > 0 && !_warnedProtectedTraits)
                {
                    _warnedProtectedTraits = true;
                    _log.Warning("Traits() does not send " + string.Join(", ", dropped) + ". Only a server with the secret key can set protected traits");
                }
                var values = Scalars(kept);
                if (values.Count == 0)
                {
                    if (dropped.Count == 0) _log.Warning("Traits() needs at least one trait");
                    return;
                }
                if (!_identityActive)
                {
                    if (_heldTraits == null) _heldTraits = new Dictionary<string, object>();
                    foreach (var pair in values) _heldTraits[pair.Key] = pair.Value;
                    return;
                }
                SendTraits(values);
            }
        }

        void SendTraits(Dictionary<string, object> values)
        {
            Touch();
            Enqueue(Build("traits", null, new Dictionary<string, object>(), -1, values));
        }

        /// <summary>
        /// Links the Steam account of this player (P-2815). Manalens checks the
        /// ticket with Steam and reads which of your apps the account owns. The
        /// player does not sign in. Make the ticket with
        /// GetAuthTicketForWebApi("manalens") and pass the bytes from the ticket
        /// callback (16 to 2048 bytes). The request goes out at the next Update()
        /// after consent, to the API host (ApiEndpoint). The answer comes in
        /// SteamLinked. A refused ticket is not sent again; a 429 with
        /// Retry-After (60 s or less) is tried once more, but not after
        /// SetConsent(false) or Reset() during the wait. Safe from any thread.
        /// </summary>
        public void LinkSteam(byte[] ticket)
        {
            lock (_gate)
            {
                if (_disabled) return;
                if (!_initialized)
                {
                    _log.Warning("call Init() first");
                    return;
                }
                if (ticket == null || ticket.Length < 16 || ticket.Length > 2048)
                {
                    _log.Warning("LinkSteam() needs a ticket of 16 to 2048 bytes");
                    return;
                }
                if (_linkInFlight) return;
                var hex = new StringBuilder(ticket.Length * 2);
                foreach (var b in ticket) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                _pendingLinkTicket = hex.ToString();
            }
        }

        /// <summary>true while a LinkSteam() request waits, runs or waits for its retry.</summary>
        public bool IsLinking { get { lock (_gate) return _linkInFlight || _pendingLinkTicket != null; } }

        /// <summary>Forgets the player: ends the session, clears the user ID, makes a new anonymous ID and starts a new session.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _heldTraits = null;
                _pendingLinkTicket = null;
                if (!_identityActive)
                {
                    _userId = "";
                    _deferredIdentify = null;
                    _deferredTraits = null;
                    ClearErrorHistory();
                    return;
                }
                EndSession(Now()); // Sends errors_dropped of the old session.
                ClearErrorHistory();
                _userId = "";
                _anonymousId = NewId();
                _state.Set("identity.user_id", "");
                _state.Set("identity.anonymous_id", _anonymousId);
                StartSession(Now());
                SaveState();
            }
        }

        /// <summary>
        /// Consent mode: true starts the persistent identity and sends game_open;
        /// false deletes the ID, the session and every queued event. The choice is
        /// saved for the next launch.
        /// </summary>
        public void SetConsent(bool granted)
        {
            lock (_gate)
            {
                if (_disabled) return;
                if (!_initialized)
                {
                    _pendingConsent = granted;
                    return;
                }
                ApplyConsent(granted);
            }
        }

        /// <summary>Asks for a send at the next Update(). Safe from any thread.</summary>
        public void RequestFlush()
        {
            lock (_gate) _flushRequested = true;
        }

        /// <summary>Sends one batch now, if the SDK is idle and not backing off. Call it on the transport's thread.</summary>
        public void SendNow()
        {
            lock (_gate)
            {
                _flushRequested = true;
                TrySend();
            }
        }

        /// <summary>
        /// Call every frame (Unity: Update). Sends the captured engine errors,
        /// keeps the session alive while focused, saves the state and the queue
        /// every FlushIntervalSeconds, and sends a batch when one is due.
        /// </summary>
        public void Update()
        {
            lock (_gate)
            {
                if (!_initialized || _disabled) return;
                DrainLogs();
                var now = Now();
                if (now >= _nextTickAt)
                {
                    _nextTickAt = now + TickInterval();
                    if (_optOutWritePending) WriteOptOut();
                    OnTick();
                }
                if (_flushRequested) TrySend();
                UpdateLink();
            }
        }

        /// <summary>
        /// The game lost or got focus (desktop focus, or a mobile pause and
        /// resume). On loss the SDK saves its state and starts a send.
        /// </summary>
        public void OnFocusChanged(bool focused)
        {
            lock (_gate)
            {
                if (!_initialized || _disabled || !_identityActive) return;
                if (focused == _focused && !focused) return; // Pause and focus loss arrive together on mobile.
                if (focused)
                {
                    _focused = true;
                    // Only an open session: after Shutdown() the next event starts a new one.
                    if (_sessionId != "") Touch();
                    return;
                }
                _focused = false;
                _lastActivityAt = Now();
                SaveSession();
                SaveQueue();
                _flushRequested = true;
                TrySend();
            }
        }

        /// <summary>
        /// Starts a clean shutdown: ends the session (session_end), saves the
        /// queue and starts a send. Then call Update() until IsIdle or a timeout.
        /// </summary>
        public void BeginShutdown()
        {
            lock (_gate)
            {
                if (!_identityActive) return;
                EndSession(Now());
                SaveQueue();
                _backoffUntil = 0;
                _flushRequested = true;
                TrySend();
            }
        }

        /// <summary>
        /// The process is quitting (Unity: Application.quitting). Ends an open
        /// session and keeps every unsent event on disk for the next launch.
        /// </summary>
        public void OnQuit()
        {
            lock (_gate)
            {
                if (!_initialized || _disabled || !_identityActive) return;
                if (_sessionId != "") EndSession(Now());
                SaveQueue();
            }
        }

        /// <summary>
        /// Queues an engine log message (exception, error, assert or warning)
        /// as an automatic error. Safe from any thread and cheap: the error is
        /// sent at the next Update().
        /// </summary>
        public void CaptureLog(string message, string stack, LogKind kind)
        {
            if (!_options.CaptureErrors) return;
            if (kind == LogKind.Warning && !_options.CaptureWarnings) return;
            message = message ?? "";
            if (message.StartsWith(LogPrefix, StringComparison.Ordinal)) return; // The SDK's own logs: never send them (no loop).
            lock (_logGate)
            {
                if (_pendingLogs.Count < MaxPendingLogs) _pendingLogs.Add(new PendingLog { Message = message, Stack = stack ?? "", Kind = kind });
                else _logOverflow++;
            }
        }

        /// <summary>Changes the collector URL (for example after a config download).</summary>
        public void SetEndpoint(string endpoint)
        {
            lock (_gate)
            {
                if (SetUrl((endpoint ?? "").Trim()))
                {
                    _backoffUntil = 0;
                    _failures = 0;
                }
            }
        }

        /// <summary>
        /// Adds this install's handoff parameter (mana_hid=&lt;anonymous ID&gt;.&lt;unix seconds&gt;)
        /// to a website URL, so the website joins the browser to this player.
        /// Before consent (or before Init) the URL is returned unchanged.
        /// </summary>
        public string LinkUrl(string url)
        {
            lock (_gate)
            {
                if (url == null || !_identityActive || _anonymousId == "") return url;
                var fragment = "";
                var hashAt = url.IndexOf('#');
                if (hashAt >= 0)
                {
                    fragment = url.Substring(hashAt);
                    url = url.Substring(0, hashAt);
                }
                var separator = url.Contains("?") ? "&" : "?";
                if (url.EndsWith("?", StringComparison.Ordinal) || url.EndsWith("&", StringComparison.Ordinal)) separator = "";
                var seconds = ((long)Math.Floor(Now())).ToString(CultureInfo.InvariantCulture);
                return url + separator + HandoffParam + "=" + _anonymousId + "." + seconds + fragment;
            }
        }

        public bool IsInitialized { get { lock (_gate) return _initialized; } }
        /// <summary>true when Init() ran with Enabled = false.</summary>
        public bool IsDisabled { get { lock (_gate) return _disabled; } }
        /// <summary>true when the SDK records events (initialized, enabled and consent given or not required).</summary>
        public bool IsActive { get { lock (_gate) return _identityActive; } }
        public string AnonymousId { get { lock (_gate) return _anonymousId; } }
        public string UserId { get { lock (_gate) return _userId; } }
        public string SessionId { get { lock (_gate) return _sessionId; } }
        public int QueueSize { get { lock (_gate) return _queue.Count; } }
        public bool IsSending { get { lock (_gate) return _sending; } }
        /// <summary>The result of the last send, for example "HTTP 202: 12 events stored". "" before the first response.</summary>
        public string LastSendResult { get { lock (_gate) return _lastSendResult; } }
        /// <summary>The JSON of the last RecentCount queued events, oldest first, also after they were sent.</summary>
        public string[] RecentEvents { get { lock (_gate) return _recent.ToArray(); } }
        /// <summary>No request in flight, and nothing to send now (empty queue, or waiting after a failure).</summary>
        public bool IsIdle { get { lock (_gate) return !_sending && (_queue.Count == 0 || Now() < _backoffUntil || !_identityActive); } }
        public ManalensOptions Options => _options;

        // Identity and sessions ----------------------------------------------

        bool IdentityAllowed()
        {
            // Fail closed: an opt-out in this run, an opt-out marker (or one that
            // could not be read), or a state file that exists but is unreadable.
            if (_withdrawnThisRun || _optOutMarker || _stateUnreadable || _stateCorrupt) return false;
            if (_state.Has("consent.granted")) return _state.GetBool("consent.granted", false);
            return !_options.ConsentRequired;
        }

        bool ReadyToRecord()
        {
            if (_disabled) return false;
            if (!_initialized)
            {
                _log.Warning("call Init() first");
                return false;
            }
            return _identityActive; // No events at all before consent.
        }

        void StartIdentity()
        {
            _identityActive = true;
            var now = Now();
            _anonymousId = _state.Get("identity.anonymous_id");
            var firstOpen = _anonymousId == "";
            if (firstOpen)
            {
                _anonymousId = NewId();
                _state.Set("identity.anonymous_id", _anonymousId);
                _state.Set("identity.first_open_at", Ids.Iso(now));
            }
            _userId = _state.Get("identity.user_id");
            LoadQueue();
            // A session left open by a crash or a killed process ends at its last activity.
            var previous = _state.Get("session.id");
            if (previous != "")
            {
                _sessionId = previous;
                _sessionStartedAt = _state.GetDouble("session.started_at", now);
                _lastActivityAt = _state.GetDouble("session.last_activity_at", _sessionStartedAt);
                // No normal exit and the game had focus: count it as a crash (as the
                // Sentry "abnormal" session). A game the OS closed in the background
                // is not a crash. On web a closed or reloaded tab has no quit event,
                // so every relaunch would look like a crash: no crash detection there.
                if (!_serverQuiet && !_state.GetBool("session.exited", false) && _state.GetBool("session.focused", true) && _platform != "web")
                {
                    var crash = new Dictionary<string, object> { ["previous_session_id"] = previous };
                    var build = _state.Get("session.build");
                    var version = _state.Get("session.app_version");
                    if (build != "") crash["previous_build"] = build;
                    if (version != "") crash["previous_app_version"] = version;
                    Enqueue(Build("track", "app_crash", crash, _lastActivityAt));
                }
                EndSession(_lastActivityAt);
            }
            StartSession(now);
            if (!_serverQuiet)
            {
                Enqueue(Build("track", "game_open", new Dictionary<string, object>
                {
                    ["is_first_open"] = firstOpen,
                    ["first_open_at"] = _state.Get("identity.first_open_at", Ids.Iso(now)),
                }, -1));
            }
            SaveState();
            // Write game_open (the install signal) and app_crash to disk
            // before any send: a process killed before the next save would lose them.
            SaveQueue();
            if (_deferredIdentify != null)
            {
                var userId = _deferredIdentify[0];
                var traits = _deferredTraits;
                _deferredIdentify = null;
                _deferredTraits = null;
                Identify(userId, traits);
            }
            if (_heldTraits != null)
            {
                var held = _heldTraits;
                _heldTraits = null;
                SendTraits(held);
            }
            _flushRequested = true;
        }

        void StartSession(double at)
        {
            _sessionId = NewId();
            _sessionStartedAt = at;
            _lastActivityAt = at;
            _autoErrors = 0;
            SaveSession();
            if (!_serverQuiet) Enqueue(Build("track", "session_start", new Dictionary<string, object>(), at));
        }

        void EndSession(double at)
        {
            if (_sessionId == "") return;
            var duration = Math.Max(0, at - _sessionStartedAt);
            var ended = new Dictionary<string, object> { ["duration_s"] = (long)Math.Round(duration, MidpointRounding.AwayFromZero) };
            if (_errorsDropped > 0)
            {
                ended["errors_dropped"] = _errorsDropped;
                _errorsDropped = 0;
            }
            if (!_serverQuiet) Enqueue(Build("track", "session_end", ended, at));
            _sessionId = "";
            _state.RemovePrefix("session.");
            SaveState();
        }

        /// <summary>
        /// Records activity. Rotates the session after 30 minutes without
        /// activity, and when the session is 24 hours old (the collector's
        /// session rule does not use a client session of more than 24 hours).
        /// </summary>
        void Touch()
        {
            var now = Now();
            if (_sessionId == "") StartSession(now);
            else if (now - _lastActivityAt > SessionTimeoutSeconds)
            {
                EndSession(_lastActivityAt);
                StartSession(now);
            }
            else if (now - _sessionStartedAt >= SessionMaxAgeSeconds)
            {
                if (_options.Debug) _log.Info("the session is 24 hours old; a new session starts");
                EndSession(now);
                StartSession(now);
            }
            _lastActivityAt = now;
        }

        void OnTick()
        {
            if (!_identityActive) return;
            // While the game has focus the player is active: keep the session alive.
            // Only an open one: after Shutdown() a tick must not start a new session.
            if (_focused && _sessionId != "") Touch();
            SaveSession();
            SaveQueue();
            _flushRequested = true;
        }

        void SaveSession()
        {
            if (_sessionId == "") return;
            _state.Set("session.id", _sessionId);
            _state.SetDouble("session.started_at", _sessionStartedAt);
            _state.SetDouble("session.last_activity_at", _lastActivityAt);
            _state.SetBool("session.focused", _focused);
            _state.SetBool("session.exited", false);
            _state.Set("session.build", _build);
            _state.Set("session.app_version", _appVersion);
            SaveState();
        }

        /// <summary>Never overwrites a state file that could not be read or parsed.</summary>
        bool SaveState()
        {
            if (_disabled || _stateUnreadable || _stateCorrupt) return false;
            return _storage.Write(StateName, _state.Serialize());
        }

        /// <summary>
        /// Reads the opt-out marker and the state. Only a missing file means
        /// "nothing saved yet". A file that exists but cannot be read or parsed
        /// keeps the SDK off in this run and is never overwritten.
        /// </summary>
        void LoadState()
        {
            _optOutMarker = _storage.Read(OptOutName, out _) != ReadStatus.Missing;
            _state = new StateFile();
            var status = _storage.Read(StateName, out var text);
            if (status == ReadStatus.Failed)
            {
                _stateUnreadable = true;
                _log.Warning("could not read the saved state; the SDK stays off in this run and keeps the file");
            }
            else if (status == ReadStatus.Ok && !StateFile.TryParse(text, out _state))
            {
                _state = new StateFile();
                _stateCorrupt = true;
                _storage.Write(CorruptStateName, text ?? "");
                _log.Warning("the saved state is damaged; the SDK stays off until SetConsent(true). A copy is in " + CorruptStateName);
            }
        }

        /// <summary>
        /// Records the player's choice. An opt-out is written first as a marker,
        /// then in the state; when a write fails, the SDK stays off and retries
        /// the write at each tick of Update().
        /// </summary>
        void ApplyConsent(bool granted)
        {
            if (granted)
            {
                if (_stateUnreadable)
                {
                    _log.Warning("SetConsent(true) ignored: the saved state could not be read. Try again at the next launch");
                    return;
                }
                if (_optOutMarker || _optOutWritePending)
                {
                    if (!_storage.Delete(OptOutName))
                    {
                        _log.Warning("could not remove the opt-out marker; the SDK stays off");
                        return;
                    }
                    _optOutMarker = false;
                    _optOutWritePending = false;
                }
                // A damaged state was copied aside at Init: start a new one.
                _stateCorrupt = false;
                _withdrawnThisRun = false;
                _state.SetBool("consent.granted", true);
                SaveState();
                if (!_identityActive) StartIdentity();
                return;
            }
            _withdrawnThisRun = true;
            _identityActive = false;
            _deferredIdentify = null;
            _deferredTraits = null;
            _heldTraits = null;
            _pendingLinkTicket = null;
            _anonymousId = "";
            _userId = "";
            _sessionId = "";
            _queue.Clear();
            _recent.Clear();
            _queueDirty = false;
            ClearErrorHistory();
            lock (_logGate)
            {
                // Logs captured before the withdrawal are never sent.
                _pendingLogs.Clear();
                _logOverflow = 0;
            }
            WriteOptOut();
        }

        /// <summary>Event names and error counts of the old player must never reach a later error.</summary>
        void ClearErrorHistory()
        {
            _breadcrumbs.Clear();
            _errorSeen.Clear();
            _errorTimes.Clear();
            _errorsDropped = 0;
        }

        /// <summary>Writes the marker and the state, and deletes the queue. On any failure, retry later.</summary>
        void WriteOptOut()
        {
            var ok = _storage.Write(OptOutName, "opted out\n");
            if (ok) _optOutMarker = true;
            if (!_stateUnreadable && !_stateCorrupt)
            {
                _state.SetBool("consent.granted", false);
                _state.Remove("identity.anonymous_id");
                _state.Remove("identity.user_id");
                _state.Remove("identity.first_open_at");
                _state.RemovePrefix("session.");
                ok &= SaveState();
            }
            ok &= _storage.Delete(QueueName);
            _optOutWritePending = !ok;
            if (!ok) _log.Warning("could not save the opt-out; the SDK stays off and tries again");
        }

        // Events and queue ---------------------------------------------------

        /// <summary>Builds the envelope and runs BeforeSend on it. Null: BeforeSend dropped the event.</summary>
        QueueEntry Build(string type, string eventName, Dictionary<string, object> properties, double at, Dictionary<string, object> traits = null)
        {
            properties = Fit(properties, "properties");
            if (traits != null) traits = Fit(traits, "traits");
            var when = at < 0 ? Now() : at;
            var messageId = NewId();
            var userId = _userId;
            if (_beforeSend != null && !ApplyBeforeSend(type, ref eventName, ref properties, ref traits, ref userId, messageId, when))
            {
                if (_options.Debug) _log.Info("BeforeSend dropped " + type + " " + eventName);
                return null;
            }
            var sb = new StringBuilder(512);
            sb.Append("{\"type\":");
            Json.WriteString(sb, type);
            sb.Append(",\"message_id\":");
            Json.WriteString(sb, messageId);
            sb.Append(",\"channel\":\"app\",\"version\":");
            Json.WriteString(sb, LibraryVersion);
            sb.Append(",\"original_timestamp\":");
            Json.WriteString(sb, Ids.Iso(when));
            sb.Append(",\"context\":");
            WriteContext(sb);
            sb.Append(",\"properties\":");
            Json.WriteObject(sb, properties);
            sb.Append(",\"anonymous_id\":");
            Json.WriteString(sb, _anonymousId);
            if (_sessionId != "")
            {
                sb.Append(",\"session_id\":");
                Json.WriteString(sb, _sessionId);
            }
            if (!string.IsNullOrEmpty(eventName))
            {
                sb.Append(",\"event\":");
                Json.WriteString(sb, eventName);
            }
            if (userId != "")
            {
                sb.Append(",\"user_id\":");
                Json.WriteString(sb, userId);
            }
            if (traits != null)
            {
                sb.Append(",\"traits\":");
                Json.WriteObject(sb, traits);
            }
            sb.Append('}');
            if (type == "track" && eventName != "error")
            {
                _breadcrumbs.Add(eventName);
                if (_breadcrumbs.Count > BreadcrumbCount) _breadcrumbs.RemoveAt(0);
            }
            return new QueueEntry { At = when, MessageId = messageId, Json = sb.ToString() };
        }

        List<KeyValuePair<string, object>> AppValues()
        {
            var app = new List<KeyValuePair<string, object>>
            {
                new KeyValuePair<string, object>("platform", _platform),
                new KeyValuePair<string, object>("engine", Engine),
            };
            if (_appVersion != "") app.Add(new KeyValuePair<string, object>("app_version", _appVersion));
            if (_build != "") app.Add(new KeyValuePair<string, object>("build", _build));
            if (_storefront != "") app.Add(new KeyValuePair<string, object>("storefront", _storefront));
            return app;
        }

        /// <summary>
        /// Gives BeforeSend a copy of the whole event. Takes event, properties,
        /// traits and user_id from the result; keeps the other fields. Returns
        /// false to drop the event. When the result changes or removes type,
        /// message_id, anonymous_id, session_id or context.app, or has a wrong
        /// event, properties, traits or user_id, the original event is kept.
        /// </summary>
        bool ApplyBeforeSend(string type, ref string eventName, ref Dictionary<string, object> properties, ref Dictionary<string, object> traits, ref string userId, string messageId, double when)
        {
            if (_inBeforeSend)
            {
                _log.Warning("an event recorded inside BeforeSend is dropped");
                return false;
            }
            var app = new Dictionary<string, object>();
            foreach (var pair in AppValues()) app[pair.Key] = pair.Value;
            var context = new Dictionary<string, object> { ["app"] = new Dictionary<string, object>(app), ["library_name"] = LibraryName, ["library_version"] = LibraryVersion };
            if (!string.IsNullOrEmpty(_env.Locale)) context["locale"] = Left(_env.Locale.Replace('_', '-'), 32);
            var view = new Dictionary<string, object>
            {
                ["type"] = type,
                ["message_id"] = messageId,
                ["channel"] = "app",
                ["version"] = LibraryVersion,
                ["original_timestamp"] = Ids.Iso(when),
                ["context"] = context,
                ["properties"] = new Dictionary<string, object>(properties),
                ["anonymous_id"] = _anonymousId,
            };
            if (_sessionId != "") view["session_id"] = _sessionId;
            if (!string.IsNullOrEmpty(eventName)) view["event"] = eventName;
            if (userId != "") view["user_id"] = userId;
            if (traits != null) view["traits"] = new Dictionary<string, object>(traits);
            Dictionary<string, object> changed;
            _inBeforeSend = true;
            try
            {
                changed = _beforeSend(view);
            }
            catch (Exception e)
            {
                _log.Warning("BeforeSend threw (" + e.Message + "); event dropped");
                return false;
            }
            finally
            {
                _inBeforeSend = false;
            }
            if (changed == null) return false;

            var ok = Same(changed, "type", type) && Same(changed, "message_id", messageId) && Same(changed, "anonymous_id", _anonymousId)
                && Same(changed, "session_id", _sessionId == "" ? null : _sessionId);
            if (!(changed.TryGetValue("context", out var contextValue) && contextValue is IDictionary<string, object> newContext
                && newContext.TryGetValue("app", out var appValue) && appValue is IDictionary<string, object> newApp
                && newApp.Count == app.Count && app.All(pair => newApp.TryGetValue(pair.Key, out var value) && Equals(value, pair.Value))))
                ok = false;
            var newName = eventName;
            if (!string.IsNullOrEmpty(eventName))
            {
                if (changed.TryGetValue("event", out var name) && name is string text && text.Length >= 1 && text.Length <= 128) newName = text;
                else ok = false;
            }
            var newProperties = properties;
            if (!changed.TryGetValue("properties", out var props) || props == null) newProperties = new Dictionary<string, object>();
            else if (props is IDictionary<string, object> map) newProperties = Fit(Scalars(map), "properties");
            else ok = false;
            var newTraits = traits;
            if (traits != null)
            {
                if (!changed.TryGetValue("traits", out var t) || t == null) newTraits = new Dictionary<string, object>();
                else if (t is IDictionary<string, object> traitMap) newTraits = Fit(Scalars(traitMap), "traits");
                else ok = false;
            }
            var newUserId = "";
            if (changed.TryGetValue("user_id", out var user) && user != null)
            {
                if (user is string id && id.Length <= 255) newUserId = id;
                else ok = false;
            }
            if (type == "identify" && newUserId == "") ok = false;
            if (!ok)
            {
                _log.Warning("BeforeSend changed or removed type, message_id, anonymous_id, session_id, context.app, or gave a wrong event, properties, traits or user_id; the original event is sent");
                return true;
            }
            eventName = newName;
            properties = newProperties;
            traits = newTraits;
            userId = newUserId;
            return true;
        }

        static bool Same(Dictionary<string, object> values, string key, string expected)
        {
            var has = values.TryGetValue(key, out var value) && value != null;
            if (expected == null) return !has;
            return has && value is string text && text == expected;
        }

        void WriteContext(StringBuilder sb)
        {
            var app = AppValues();
            sb.Append("{\"app\":");
            Json.WriteObject(sb, app);
            var locale = _env.Locale ?? "";
            if (locale != "")
            {
                sb.Append(",\"locale\":");
                Json.WriteString(sb, Left(locale.Replace('_', '-'), 32));
            }
            // The collector accepts -720 to 840 minutes; any other value is left out.
            var offset = _env.UtcOffsetMinutes;
            if (offset.HasValue && offset.Value >= -720 && offset.Value <= 840)
            {
                sb.Append(",\"utc_offset\":");
                sb.Append(offset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            sb.Append(",\"library_name\":");
            Json.WriteString(sb, LibraryName);
            sb.Append(",\"library_version\":");
            Json.WriteString(sb, LibraryVersion);
            sb.Append('}');
        }

        /// <summary>Queues an event. False when BeforeSend dropped it (entry is null).</summary>
        bool Enqueue(QueueEntry entry)
        {
            if (entry == null) return false;
            _queue.Add(entry);
            var overflow = _queue.Count - _maxQueue;
            if (overflow > 0)
            {
                _queue.RemoveRange(0, overflow);
                RaiseSendFailed(0, overflow);
            }
            _queueDirty = true;
            _recent.Add(entry.Json);
            if (_recent.Count > RecentCount) _recent.RemoveAt(0);
            if (_options.Debug) _log.Info("queued " + entry.Json);
            if (_queue.Count >= _flushAt)
            {
                SaveQueue();
                _flushRequested = true;
            }
            return true;
        }

        /// <summary>Drops events the collector would refuse as more than 7 days old.</summary>
        void Prune()
        {
            var oldest = Now() - MaxEventAgeSeconds;
            var removed = _queue.RemoveAll(entry => entry.At < oldest);
            if (removed > 0)
            {
                _queueDirty = true;
                RaiseSendFailed(0, removed);
            }
        }

        void TrySend()
        {
            if (!_identityActive || _sending || _queue.Count == 0)
            {
                if (_queue.Count == 0) _flushRequested = false;
                return;
            }
            if (Now() < _backoffUntil) return; // Keep the request: send when the wait is over.
            Prune();
            if (_queue.Count == 0)
            {
                _flushRequested = false;
                return;
            }
            var count = Math.Min(_flushAt, _queue.Count);
            var ids = new HashSet<string>();
            var body = new StringBuilder("{\"events\":[");
            for (var i = 0; i < count; i++)
            {
                if (i > 0) body.Append(',');
                body.Append(_queue[i].Json);
                ids.Add(_queue[i].MessageId);
            }
            // sent_at: the device clock now. The collector corrects the event times with it, so a wrong clock gives right times (P-2799).
            body.Append("],\"sent_at\":");
            Json.WriteString(body, Ids.Iso(Now()));
            body.Append('}');
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["x-publishable-key"] = _options.PublishableKey,
            };
            _sending = true;
            _flushRequested = false;
            var url = _url;
            var done = false;
            try
            {
                void Complete(int status, string response, string retryAfter)
                {
                    lock (_gate)
                    {
                        if (done) return; // A transport must call back once; ignore extra calls.
                        done = true;
                        OnResponse(status, ids, response, retryAfter);
                    }
                }
                // A transport that also gives Retry-After (optional, IRetryAfterTransport) gets the longer callback.
                if (_transport is IRetryAfterTransport withRetryAfter)
                    withRetryAfter.Send(url, headers, body.ToString(), Complete);
                else
                    _transport.Send(url, headers, body.ToString(), (status, response) => Complete(status, response, null));
            }
            catch (Exception e)
            {
                _log.Warning("send failed to start (" + e.Message + ")");
                if (!done)
                {
                    done = true;
                    OnResponse(0, ids, null, null);
                }
            }
        }

        static readonly Regex RefusalCode = new Regex("\"code\"\\s*:\\s*\"([A-Z_]+)\"", RegexOptions.CultureInvariant);
        static readonly Regex RefusalMessage = new Regex("\"message\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
        static readonly Regex RejectedField = new Regex("\"rejected\"\\s*:\\s*(\\d+)", RegexOptions.CultureInvariant);

        /// <summary>
        /// The number of refused events in an accepted batch (P-2787): the
        /// collector stores the valid events and lists the others in
        /// "errors". 0 when the body has no "rejected" count.
        /// </summary>
        public static int RejectedCount(string response)
        {
            if (string.IsNullOrEmpty(response)) return 0;
            var match = RejectedField.Match(response);
            return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
        }

        /// <summary>
        /// The reason of a refusal from the collector's response body: the
        /// reason codes (for example INVALID_EVENT, SESSION_ID_REQUIRED) and
        /// the first message. "" when the body has none.
        /// </summary>
        public static string RefusalReason(string response)
        {
            if (string.IsNullOrEmpty(response)) return "";
            var codes = new List<string>();
            foreach (Match match in RefusalCode.Matches(response))
                if (!codes.Contains(match.Groups[1].Value)) codes.Add(match.Groups[1].Value);
            var message = RefusalMessage.Match(response);
            if (codes.Count == 0 && !message.Success) return "";
            var text = string.Join(", ", codes);
            if (message.Success) text += (text == "" ? "" : ": ") + message.Groups[1].Value;
            return text.Length > 300 ? text.Substring(0, 300) : text;
        }

        /// <summary>
        /// The wait in seconds that a Retry-After header asks for: an integer
        /// number of seconds, or an HTTP date (then the time until that date).
        /// A missing or invalid value, or a date in the past, gives 0. Not
        /// capped: the caller applies <see cref="MaxRetryAfterSeconds"/>.
        /// </summary>
        public static double ParseRetryAfter(string header, double nowUnixSeconds)
        {
            var value = header?.Trim();
            if (string.IsNullOrEmpty(value)) return 0;
            var digits = true;
            foreach (var c in value) digits &= c >= '0' && c <= '9';
            if (digits)
                return double.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
            if (DateTimeOffset.TryParseExact(value, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                return Math.Max(0, date.ToUnixTimeMilliseconds() / 1000.0 - nowUnixSeconds);
            return 0;
        }

        void OnResponse(int status, HashSet<string> ids, string response, string retryAfter)
        {
            var count = ids.Count;
            var ok = status >= 200 && status < 300;
            // A 4xx other than 408 and 429 will fail again: drop the batch (as the
            // Godot and web SDKs do). Network errors and 5xx stay and back off.
            var rejected = status >= 400 && status < 500 && status != 408 && status != 429;
            if (ok || rejected)
            {
                _queue.RemoveAll(entry => ids.Contains(entry.MessageId));
                _queueDirty = true;
                _failures = 0;
                _backoffUntil = 0;
                if (rejected)
                {
                    // Dropped, not sent again: the same batch would be refused again.
                    var reason = RefusalReason(response);
                    _log.Warning("collector refused " + count + " events (HTTP " + status + ")" + (reason == "" ? "" : " " + reason) + "; they are dropped");
                    RaiseSendFailed(status, count);
                }
                else
                {
                    // An accepted batch can still list refused events (P-2787). They are
                    // not sent again: the same events would be refused again.
                    var refused = Math.Min(count, RejectedCount(response));
                    if (refused > 0)
                    {
                        var reason = RefusalReason(response);
                        _log.Warning("collector refused " + refused + " of " + count + " events (HTTP " + status + ")" + (reason == "" ? "" : " " + reason) + "; they are dropped");
                        RaiseSendFailed(status, refused);
                        count -= refused;
                    }
                }
            }
            else
            {
                _failures++;
                var delay = Math.Min(MaxBackoffSeconds, Math.Pow(2, _failures));
                var wait = delay * (0.5 + 0.5 * Random());
                // Wait at least what the collector asks for (Retry-After on a 429 or 503), at most 1 hour.
                wait = Math.Max(wait, Math.Min(MaxRetryAfterSeconds, ParseRetryAfter(retryAfter, Now())));
                _backoffUntil = Now() + wait;
                if (_options.Debug) _log.Info("send failed (HTTP " + status + "); retry in " + Math.Round(wait) + " s");
            }
            _sending = false;
            var why = RefusalReason(response);
            _lastSendResult = status <= 0 ? "network error: kept in the queue, sent again later"
                : ok ? "HTTP " + status + ": " + count + " events stored" + (ids.Count > count ? ", " + (ids.Count - count) + " refused" + (why == "" ? "" : " (" + why + ")") : "")
                : "HTTP " + status + (why == "" ? "" : " " + why) + (rejected ? ": " + count + " events dropped" : ": kept in the queue, sent again later");
            SaveQueue();
            RaiseFlushed(ok ? count : 0, ok);
            if ((ok || rejected) && _queue.Count > 0) _flushRequested = true;
        }

        void LoadQueue()
        {
            if (_storage.Read(QueueName, out var text) != ReadStatus.Ok || string.IsNullOrEmpty(text)) return;
            foreach (var line in text.Split('\n'))
            {
                // "<unix seconds>\t<message id>\t<event JSON>"; the JSON writer escapes tabs and newlines.
                var first = line.IndexOf('\t');
                var second = first < 0 ? -1 : line.IndexOf('\t', first + 1);
                if (second < 0) continue;
                if (!double.TryParse(line.Substring(0, first), NumberStyles.Float, CultureInfo.InvariantCulture, out var at)) continue;
                var json = line.Substring(second + 1);
                if (!json.StartsWith("{", StringComparison.Ordinal) || !json.EndsWith("}", StringComparison.Ordinal)) continue;
                _queue.Add(new QueueEntry { At = at, MessageId = line.Substring(first + 1, second - first - 1), Json = json });
            }
            var overflow = _queue.Count - _maxQueue;
            if (overflow > 0)
            {
                _queue.RemoveRange(0, overflow);
                RaiseSendFailed(0, overflow);
            }
            Prune();
        }

        void SaveQueue()
        {
            if (!_queueDirty || !_identityActive) return;
            if (_queue.Count == 0) _storage.Delete(QueueName);
            else
            {
                var sb = new StringBuilder();
                foreach (var entry in _queue)
                    sb.Append(entry.At.ToString("R", CultureInfo.InvariantCulture)).Append('\t').Append(entry.MessageId).Append('\t').Append(entry.Json).Append('\n');
                _storage.Write(QueueName, sb.ToString());
            }
            _queueDirty = false;
        }

        // Errors -------------------------------------------------------------

        void DrainLogs()
        {
            List<PendingLog> items;
            int overflow;
            lock (_logGate)
            {
                if (_pendingLogs.Count == 0 && _logOverflow == 0) return;
                items = _pendingLogs;
                overflow = _logOverflow;
                _pendingLogs = new List<PendingLog>();
                _logOverflow = 0;
            }
            if (!_identityActive) return;
            _errorsDropped += overflow + Math.Max(0, items.Count - AutoErrorsPerUpdate);
            for (var i = 0; i < items.Count && i < AutoErrorsPerUpdate; i++)
            {
                var item = items[i];
                // Play mode also logs the editor's own errors (the inspector, search indexing): not the game's.
                if (ErrorTools.IsEditorStack(item.Stack)) continue;
                var frame = ErrorTools.FirstFrame(item.Stack);
                var exceptionType = item.Kind == LogKind.Exception ? ErrorTools.ExceptionType(item.Message) : "";
                var errorType = exceptionType;
                if (errorType == "") errorType = item.Kind == LogKind.Exception ? "exception" : item.Kind == LogKind.Assert ? "assert" : item.Kind == LogKind.Warning ? "log_warning" : "log_error";
                // A log line can hold paths, URLs, emails and tokens: clean it before
                // it is queued. The fingerprint is computed from the cleaned text, so
                // the same error on two machines with different user names groups together.
                // The exception type at the start (for example InvalidOperationException,
                // 25 characters) is a class name, not a token: it is kept.
                var message = exceptionType != "" && item.Message.StartsWith(exceptionType, StringComparison.Ordinal)
                    ? exceptionType + ErrorTools.CleanLogMessage(item.Message.Substring(exceptionType.Length), _homeDirs)
                    : ErrorTools.CleanLogMessage(item.Message, _homeDirs);
                RecordError(message, new TrackErrorOptions
                {
                    Severity = item.Kind == LogKind.Warning ? "warning" : "error",
                    ErrorType = errorType,
                    Handled = false,
                    Stack = item.Stack,
                    File = frame.File,
                    Line = frame.Line,
                    Function = frame.Function,
                    Properties = new Dictionary<string, object> { ["log_type"] = item.Kind.ToString().ToLowerInvariant() },
                }, SourceAuto);
            }
        }

        void RecordError(string message, TrackErrorOptions options, string source)
        {
            if (_disabled) return;
            if (!_initialized || !_identityActive)
            {
                if (source == SourceManual) ReadyToRecord(); // Warns before Init().
                return;
            }
            var severity = Array.IndexOf(Severities, options.Severity) >= 0 ? options.Severity : "error";
            var errorType = Left(options.ErrorType ?? "", 128);
            var file = Left(options.File ?? "", 256);
            var function = Left(options.Function ?? "", 256);
            // The error's own keys come first, so a trim to the limits (Fit) drops custom keys first.
            var props = new Dictionary<string, object>();
            props["severity"] = severity;
            props["message"] = Left(message, MaxString);
            props["handled"] = options.Handled;
            props["source"] = source;
            var fingerprint = Left(options.Fingerprint ?? "", 128);
            if (fingerprint == "") fingerprint = ErrorTools.Fingerprint(errorType, message, file, function);
            props["fingerprint"] = fingerprint;
            if (errorType != "") props["error_type"] = errorType;
            if (file != "")
            {
                props["file"] = file;
                props["line"] = options.Line;
            }
            if (function != "") props["function"] = function;
            var stack = (options.Stack ?? "").Trim();
            if (stack != "") props["stack"] = Left(stack, MaxString);
            if (_breadcrumbs.Count > 0)
            {
                var crumbs = string.Join(",", _breadcrumbs);
                props["breadcrumbs"] = crumbs.Length > MaxString ? crumbs.Substring(crumbs.Length - MaxString) : crumbs;
            }
            foreach (var pair in Scalars(options.Properties))
                if (!props.ContainsKey(pair.Key)) props[pair.Key] = pair.Value;
            if (!ErrorAllowed(fingerprint, source))
            {
                _errorsDropped++;
                return;
            }
            Touch();
            // BeforeSend runs in Build, after the fingerprint and the limits.
            if (!Enqueue(Build("track", "error", props, -1))) return;
            if (source == SourceAuto) _autoErrors++;
            // Keep the error on disk now: a crash can follow it.
            SaveQueue();
        }

        /// <summary>The limits. Manual errors skip only the per-session cap.</summary>
        bool ErrorAllowed(string fingerprint, string source)
        {
            var now = _clock.Milliseconds;
            if (_errorSeen.TryGetValue(fingerprint, out var last) && now - last < ErrorDedupeMs) return false;
            _errorTimes.RemoveAll(at => now - at >= ErrorWindowMs);
            if (_errorTimes.Count >= ErrorWindowMax) return false;
            if (source == SourceAuto && _autoErrors >= _options.MaxAutoErrorsPerSession) return false;
            _errorSeen[fingerprint] = now;
            _errorTimes.Add(now);
            if (_errorSeen.Count > 500) _errorSeen.Clear();
            return true;
        }

        // Helpers ------------------------------------------------------------

        /// <summary>
        /// Keeps scalar values: string (cut to 1024 characters), bool, null and
        /// finite numbers. Enums become their name. The collector refuses nested
        /// objects and arrays, so the SDK drops them with a warning.
        /// </summary>
        Dictionary<string, object> Scalars(IDictionary<string, object> values)
        {
            var output = new Dictionary<string, object>();
            if (values == null) return output;
            foreach (var pair in values)
            {
                var name = pair.Key;
                if (string.IsNullOrEmpty(name) || name.Length > 100) continue;
                var value = pair.Value;
                switch (value)
                {
                    case null:
                    case bool _:
                    case int _:
                    case long _:
                    case short _:
                    case byte _:
                    case sbyte _:
                    case ushort _:
                    case uint _:
                    case ulong _:
                    case decimal _:
                        output[name] = value;
                        break;
                    case float f:
                        if (!float.IsNaN(f) && !float.IsInfinity(f)) output[name] = f;
                        break;
                    case double d:
                        if (!double.IsNaN(d) && !double.IsInfinity(d)) output[name] = d;
                        break;
                    case string s:
                        output[name] = Left(s, MaxString);
                        break;
                    case char c:
                        output[name] = c.ToString();
                        break;
                    case Enum e:
                        output[name] = e.ToString();
                        break;
                    default:
                        _log.Warning("property " + name + " is not a scalar; dropped");
                        break;
                }
            }
            return output;
        }

        /// <summary>
        /// Keeps keys in order while the object has at most MaxPropertyKeys keys
        /// and MaxPropertyBytes of JSON. The collector refuses a whole batch for
        /// one event over a limit, so the SDK drops the extra keys with a warning.
        /// </summary>
        Dictionary<string, object> Fit(Dictionary<string, object> values, string field)
        {
            if (values == null || (values.Count <= MaxPropertyKeys && JsonBytes(values) <= MaxPropertyBytes)) return values;
            var output = new Dictionary<string, object>();
            var bytes = 2; // {}
            foreach (var pair in values)
            {
                if (output.Count >= MaxPropertyKeys) break;
                var entry = JsonBytes(new[] { pair }) - 2 + (output.Count > 0 ? 1 : 0);
                if (bytes + entry > MaxPropertyBytes) break;
                output[pair.Key] = pair.Value;
                bytes += entry;
            }
            _log.Warning(field + " over the limit of " + MaxPropertyKeys + " keys or " + MaxPropertyBytes + " bytes; " + (values.Count - output.Count) + " keys dropped");
            return output;
        }

        static int JsonBytes(IEnumerable<KeyValuePair<string, object>> values)
        {
            var sb = new StringBuilder();
            Json.WriteObject(sb, values);
            return Encoding.UTF8.GetByteCount(sb.ToString());
        }

        string CheckLabel(string name, string value, int maxLength, bool warn)
        {
            value = value ?? "";
            if (value != "" && (value.Length > maxLength || !LabelRe.IsMatch(value)))
            {
                if (warn) _log.Warning(name + " must be 1 to " + maxLength + " letters, digits, spaces or . _ + -; ignored");
                return "";
            }
            return value;
        }

        bool SetUrl(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                _log.Error("Endpoint must be an http or https URL");
                return false;
            }
            _url = endpoint.TrimEnd('/') + "/v0/events";
            // The API host of LinkSteam(): ApiEndpoint, or the endpoint with an events. host changed to api.
            var api = (_options.ApiEndpoint ?? "").Trim();
            if (api == "")
            {
                var builder = new UriBuilder(uri);
                if (builder.Host.StartsWith("events.", StringComparison.OrdinalIgnoreCase)) builder.Host = "api." + builder.Host.Substring("events.".Length);
                api = builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
            }
            _linkUrl = api.TrimEnd('/') + "/v0/link";
            return true;
        }

        /// <summary>The URL of LinkSteam().</summary>
        public string LinkSteamUrl { get { lock (_gate) return _linkUrl; } }

        // Steam link (P-2815) ------------------------------------------------

        /// <summary>Called from Update(): sends a pending link, or the one retry after a 429.</summary>
        void UpdateLink()
        {
            if (_linkRetryAt >= 0 && Now() >= _linkRetryAt)
            {
                _linkRetryAt = -1;
                // The same gates as the first send: after SetConsent(false), an
                // opt-out or Reset() the old ticket and ID are not sent again.
                if (!_identityActive) { FinishLink(false, 0, "CONSENT_REQUIRED"); return; }
                if (_anonymousId != _linkAnonymousId) { FinishLink(false, 0, "IDENTITY_CHANGED"); return; }
                SendLink();
                return;
            }
            if (_pendingLinkTicket == null || _linkInFlight || !_identityActive || _anonymousId == "") return;
            var body = new StringBuilder("{\"provider\":\"steam\",\"ticket\":");
            Json.WriteString(body, _pendingLinkTicket);
            body.Append(",\"anonymous_id\":");
            Json.WriteString(body, _anonymousId);
            body.Append('}');
            _pendingLinkTicket = null;
            _linkBody = body.ToString();
            _linkAnonymousId = _anonymousId;
            _linkAttempt = 0;
            _linkInFlight = true;
            SendLink();
        }

        void SendLink()
        {
            var headers = new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json",
                ["x-publishable-key"] = _options.PublishableKey,
            };
            var done = false;
            void Complete(int status, string response, string retryAfter)
            {
                lock (_gate)
                {
                    if (done) return;
                    done = true;
                    OnLinkResponse(status, response, retryAfter);
                }
            }
            try
            {
                if (_transport is IRetryAfterTransport withRetryAfter)
                    withRetryAfter.Send(_linkUrl, headers, _linkBody, Complete);
                else
                    _transport.Send(_linkUrl, headers, _linkBody, (status, response) => Complete(status, response, null));
            }
            catch (Exception e)
            {
                _log.Warning("link request failed to start (" + e.Message + ")");
                if (!done)
                {
                    done = true;
                    OnLinkResponse(0, null, null);
                }
            }
        }

        void OnLinkResponse(int status, string response, string retryAfter)
        {
            if (status >= 200 && status < 300)
            {
                if (_options.Debug) _log.Info("Steam account linked");
                FinishLink(true, status, "");
                return;
            }
            if (status <= 0)
            {
                FinishLink(false, 0, "NETWORK_ERROR");
                return;
            }
            var wait = string.IsNullOrEmpty(retryAfter) ? -1 : ParseRetryAfter(retryAfter, Now());
            if (status == 429 && _linkAttempt == 0 && !string.IsNullOrEmpty(retryAfter) && wait >= 0 && wait <= LinkMaxRetryAfterSeconds)
            {
                _linkAttempt = 1;
                _linkRetryAt = Now() + wait;
                return;
            }
            var match = response == null ? null : RefusalCode.Match(response);
            var code = match != null && match.Success ? match.Groups[1].Value : "";
            _log.Warning("LinkSteam() failed (HTTP " + status + (code == "" ? "" : " " + code) + ")");
            FinishLink(false, status, code);
        }

        void FinishLink(bool linked, int status, string code)
        {
            _linkInFlight = false;
            _linkBody = null;
            try { SteamLinked?.Invoke(linked, status, code); }
            catch (Exception e) { _log.Warning("a SteamLinked handler threw: " + e.Message); }
        }

        double TickInterval() => Math.Max(1, _options.FlushIntervalSeconds);
        double Now() => _clock.UnixSeconds;
        static string Left(string value, int length) => value.Length > length ? value.Substring(0, length) : value;

        void RaiseFlushed(int sent, bool ok)
        {
            try { Flushed?.Invoke(sent, ok); }
            catch (Exception e) { _log.Warning("a Flushed handler threw: " + e.Message); }
        }

        void RaiseSendFailed(int status, int dropped)
        {
            try { SendFailed?.Invoke(status, dropped); }
            catch (Exception e) { _log.Warning("a SendFailed handler threw: " + e.Message); }
        }
    }
}
