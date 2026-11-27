using System.Collections;
using UnityEngine;

namespace ManalensSdk
{
    /// <summary>
    /// The hidden GameObject that drives the SDK: Update, focus and pause,
    /// quit, and the engine log capture. Manalens.Init() creates it.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class ManalensDriver : MonoBehaviour
    {
        internal AnalyticsClient Client;
        bool _closing;
        bool _quitAllowed;
        bool _paused;

        void OnEnable()
        {
            Application.logMessageReceivedThreaded += OnLog;
            Application.wantsToQuit += OnWantsToQuit;
            Application.quitting += OnQuitting;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.pauseStateChanged += OnEditorPause;
#endif
        }

        void OnDisable()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Application.wantsToQuit -= OnWantsToQuit;
            Application.quitting -= OnQuitting;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.pauseStateChanged -= OnEditorPause;
#endif
            // Leaving Play mode (or the driver is gone): stop the hang watchdog thread.
            Client?.StopHangWatchdog();
        }

#if UNITY_EDITOR
        /// <summary>The editor's pause button stops the frames: not a hang.</summary>
        void OnEditorPause(UnityEditor.PauseState state)
        {
            Client?.SetHangPaused(state == UnityEditor.PauseState.Paused);
        }
#endif

        void Update()
        {
            Client?.Update();
            SteamLink.Tick(Client);
        }

        // Desktop: focus. Mobile: pause comes with focus loss; one flag keeps a single state change (as Sentry).
        void OnApplicationFocus(bool hasFocus)
        {
            if (Client == null || _paused) return;
            Client.OnFocusChanged(hasFocus);
        }

        void OnApplicationPause(bool pauseStatus)
        {
            if (Client == null) return;
            _paused = pauseStatus;
            Client.OnFocusChanged(!pauseStatus);
        }

        /// <summary>
        /// Desktop players: delay the quit to send session_end and flush (as
        /// PostHog Unity). Never in the editor: Application.Quit() does nothing
        /// there, so a refused quit would block leaving Play mode.
        /// </summary>
        bool OnWantsToQuit()
        {
            if (Client == null || _quitAllowed || Application.isEditor || !Client.Options.HandleQuit || !Client.IsActive) return true;
            if (!_closing)
            {
                _closing = true;
                StartCoroutine(CloseRoutine());
            }
            return false;
        }

        IEnumerator CloseRoutine()
        {
            yield return ShutdownRoutine();
            _quitAllowed = true;
            Application.Quit();
        }

        /// <summary>Ends the session and sends the queue for at most QuitTimeoutSeconds (real time).</summary>
        internal IEnumerator ShutdownRoutine()
        {
            if (Client == null) yield break;
            Client.BeginShutdown();
            var deadline = Time.realtimeSinceStartup + (float)Client.Options.QuitTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                Client.Update();
                if (Client.IsIdle) break;
                yield return null;
            }
        }

        /// <summary>Always the last call (also when leaving Play mode). Not called when a mobile OS kills the app.</summary>
        void OnQuitting()
        {
            Client?.OnQuit();
        }

        /// <summary>Any thread. Only copies the message: the SDK sends it at the next Update.</summary>
        void OnLog(string condition, string stackTrace, LogType type)
        {
            var client = Client;
            if (client == null) return;
            switch (type)
            {
                case LogType.Exception: client.CaptureLog(condition, stackTrace, LogKind.Exception); break;
                case LogType.Error: client.CaptureLog(condition, stackTrace, LogKind.Error); break;
                case LogType.Assert: client.CaptureLog(condition, stackTrace, LogKind.Assert); break;
                case LogType.Warning: client.CaptureLog(condition, stackTrace, LogKind.Warning); break;
            }
        }
    }
}
