using System;
using System.Threading;

namespace ManalensSdk
{
    /// <summary>
    /// Hang detection (P-2833). The main thread writes a heartbeat once per
    /// frame (<see cref="Beat"/>). A watchdog thread checks it
    /// (<see cref="Check"/>). When the heartbeat is older than the threshold,
    /// the main thread is stuck: the watchdog calls the <c>onStuck</c>
    /// callback at once, on its own thread, so the hang can be written to
    /// disk before the player kills the game. One hang per stuck period. The
    /// next <see cref="Beat"/> after it gives the hang back with its duration.
    ///
    /// Thread safety: every member takes one lock of its own. It never takes
    /// the lock of <see cref="AnalyticsClient"/>, so a main thread that is stuck
    /// inside the SDK cannot stop the watchdog.
    /// </summary>
    public sealed class HangWatchdog
    {
        /// <summary>A hang is not counted in the first frames after the start (a long first load).</summary>
        public const int WarmupFrames = 10;
        /// <summary>The watchdog checks the heartbeat this often (at most).</summary>
        public const int PollMs = 250;

        /// <summary>One stuck period of the main thread.</summary>
        public sealed class Hang
        {
            /// <summary>The message_id of the error event (made when the hang starts, so a crash can link to it).</summary>
            public string MessageId;
            /// <summary>The session of the last heartbeat.</summary>
            public string SessionId;
            /// <summary>Unix seconds of the last heartbeat before the hang.</summary>
            public double StartedAt;
            public double ThresholdSeconds;
            /// <summary>The breadcrumbs of the last heartbeat.</summary>
            public string Breadcrumbs;
            /// <summary>Set by Beat() when the main thread is back: how long it was stuck.</summary>
            public long DurationMs;
        }

        readonly object _gate = new object();
        readonly IClock _clock;
        readonly long _thresholdMs;
        readonly int _pollMs;
        readonly Action<Hang> _onStuck;
        long _beat;
        long _lastCheck = -1;
        int _frames;
        bool _counting, _paused, _stop;
        Hang _current;
        string _session = "", _crumbs = "";
        Thread _thread;

        /// <summary>
        /// true while a debugger is attached: a breakpoint stops the main
        /// thread, and that is not a hang. Called on the watchdog thread.
        /// </summary>
        public Func<bool> DebuggerAttached = () => System.Diagnostics.Debugger.IsAttached;

        public HangWatchdog(double thresholdSeconds, IClock clock, Action<Hang> onStuck)
        {
            _thresholdMs = Math.Max(1, (long)(thresholdSeconds * 1000));
            _pollMs = (int)Math.Max(10, Math.Min(PollMs, _thresholdMs / 4));
            _clock = clock;
            _onStuck = onStuck;
            _beat = clock.Milliseconds;
        }

        public double ThresholdSeconds => _thresholdMs / 1000.0;

        /// <summary>
        /// Main thread, once per frame. <c>counting</c>: the game is focused,
        /// not paused and the SDK records. Returns the hang that just ended
        /// (with DurationMs), or null.
        /// </summary>
        public Hang Beat(bool counting, string sessionId)
        {
            lock (_gate)
            {
                var now = _clock.Milliseconds;
                var ended = _current;
                if (ended != null) ended.DurationMs = now - _beat;
                _current = null;
                _beat = now;
                if (_frames <= WarmupFrames) _frames++;
                _counting = counting && _frames > WarmupFrames;
                _session = sessionId ?? "";
                return ended;
            }
        }

        /// <summary>
        /// Any thread: no hang until <c>paused</c> is false again (the app
        /// is in the background, or the editor is paused). The time paused does
        /// not count.
        /// </summary>
        public void SetPaused(bool paused)
        {
            lock (_gate)
            {
                _paused = paused;
                _beat = _clock.Milliseconds;
            }
        }

        /// <summary>The breadcrumbs that a hang carries (main thread, when they change).</summary>
        public void SetBreadcrumbs(string breadcrumbs)
        {
            lock (_gate) _crumbs = breadcrumbs ?? "";
        }

        /// <summary>
        /// Runs <c>delete</c> (the saved hang) unless a new hang started since
        /// the last Beat(): its file must stay. Under the lock, so the watchdog
        /// cannot write a new hang between the check and the delete.
        /// <c>forget</c> (consent withdrawn): delete it in any case, and forget
        /// the hang of the stuck period that runs now.
        /// </summary>
        public void ClearSaved(Action delete, bool forget = false)
        {
            lock (_gate)
            {
                if (forget) _current = null;
                if (_current == null) delete();
            }
        }

        /// <summary>The hang of the stuck period that runs now, or null.</summary>
        public Hang Current
        {
            get { lock (_gate) return _current; }
        }

        /// <summary>
        /// One check (the watchdog thread; tests call it directly). Calls
        /// onStuck when the main thread is stuck for the first time in this
        /// stuck period.
        /// </summary>
        public void Check()
        {
            lock (_gate)
            {
                var now = _clock.Milliseconds;
                // This thread itself slept much longer than asked: the whole
                // process was stopped (system sleep, a mobile app in the
                // background). Not a hang.
                if (_lastCheck >= 0 && now - _lastCheck > _pollMs * 4 + 1000) _beat = now;
                _lastCheck = now;
                if (!_counting || _paused || _current != null || now - _beat < _thresholdMs) return;
                if (DebuggerAttached != null && DebuggerAttached())
                {
                    _beat = now;
                    return;
                }
                _current = new Hang
                {
                    MessageId = Ids.NewUuid(),
                    SessionId = _session,
                    StartedAt = _clock.UnixSeconds - (now - _beat) / 1000.0,
                    ThresholdSeconds = ThresholdSeconds,
                    Breadcrumbs = _crumbs,
                };
                // Under the lock: the main thread cannot take the hang back
                // (Beat) before it is on disk.
                try { _onStuck?.Invoke(_current); }
                catch (Exception) { /* Never stop the watchdog. */ }
            }
        }

        /// <summary>Starts the watchdog thread (a background thread: it never keeps the process alive).</summary>
        public void Start()
        {
            lock (_gate)
            {
                if (_thread != null) return;
                _stop = false;
                _thread = new Thread(Run) { IsBackground = true, Name = "Manalens hang watchdog" };
            }
            _thread.Start();
        }

        public void Stop()
        {
            Thread thread;
            lock (_gate)
            {
                _stop = true;
                thread = _thread;
                _thread = null;
            }
            thread?.Join(1000);
        }

        void Run()
        {
            while (true)
            {
                Thread.Sleep(_pollMs);
                lock (_gate)
                {
                    if (_stop) return;
                }
                Check();
            }
        }
    }
}
