using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ManalensSdk
{
    /// <summary>
    /// Stores named text blobs (the state and the queue). The Unity layer uses
    /// files in Application.persistentDataPath, or PlayerPrefs on WebGL.
    /// </summary>
    public interface IStorage
    {
        /// <summary>
        /// Reads a blob. Missing: it does not exist (content is null). Failed:
        /// it exists but could not be read. The SDK treats Failed as "unknown",
        /// never as "missing", so a read error can never undo an opt-out.
        /// </summary>
        ReadStatus Read(string name, out string content);
        /// <summary>Replaces the blob atomically (a temporary copy, then a rename). Returns false when the write failed.</summary>
        bool Write(string name, string content);
        /// <summary>Returns false when the blob still exists after the call.</summary>
        bool Delete(string name);
    }

    public enum ReadStatus
    {
        Missing,
        Ok,
        Failed,
    }

    /// <summary>
    /// Sends one HTTP POST. Call <c>onComplete</c> exactly once with the HTTP
    /// status, or 0 for a network error (no response), and the response body
    /// (or null). The SDK reads the body only to log why the collector refused
    /// a batch. The Unity transport must
    /// be called on the main thread; the SDK sends only from Update() and from
    /// the lifecycle hooks, which run on the main thread.
    /// </summary>
    public interface ITransport
    {
        void Send(string url, IReadOnlyDictionary<string, string> headers, string body, Action<int, string> onComplete);
    }

    /// <summary>
    /// Optional: a transport that also gives the <c>Retry-After</c> response
    /// header. Call <c>onComplete</c> exactly once with the HTTP status (0 for
    /// a network error), the response body (or null), and the raw value of the
    /// <c>Retry-After</c> header (or null). When a transport implements this
    /// interface, the SDK calls this method and not
    /// <see cref="ITransport.Send"/>. After a retried response (network error,
    /// 408, 429, 5xx) the SDK waits at least <c>Retry-After</c>, at most 1 hour.
    /// A transport that implements only <see cref="ITransport"/> still works:
    /// the SDK then uses only its own backoff.
    /// </summary>
    public interface IRetryAfterTransport : ITransport
    {
        void Send(string url, IReadOnlyDictionary<string, string> headers, string body, Action<int, string, string> onComplete);
    }

    /// <summary>Wall time (event times) and a monotonic clock (error limits).</summary>
    public interface IClock
    {
        /// <summary>Unix time in seconds.</summary>
        double UnixSeconds { get; }
        /// <summary>A monotonic time in milliseconds.</summary>
        long Milliseconds { get; }
    }

    public interface ILog
    {
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }

    public sealed class SystemClock : IClock
    {
        static readonly Stopwatch Watch = Stopwatch.StartNew();
        public double UnixSeconds => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        public long Milliseconds => Watch.ElapsedMilliseconds;
    }

    public sealed class NullLog : ILog
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }

    /// <summary>What the engine layer knows about the running build.</summary>
    public sealed class AppEnvironment
    {
        /// <summary>windows, macos, linux, android, ios, web, ps5, xbox_series, xbox_one, switch, switch2 or other.</summary>
        public string Platform = "other";
        /// <summary>For example en-US. Null or empty: not sent.</summary>
        public string Locale;
        /// <summary>The device clock's offset from UTC in minutes (480 for UTC+8). Null: not sent. Used only for install matching.</summary>
        public int? UtcOffsetMinutes;
        /// <summary>Used when the options have no AppVersion (Unity: Application.version).</summary>
        public string DefaultAppVersion;
        /// <summary>
        /// true in a dedicated server player: a build with the UNITY_SERVER
        /// define, or a player started in batch mode (Application.isBatchMode).
        /// Never true in the Unity Editor, also not in editor batch runs (tests, builds).
        /// </summary>
        public bool IsServerBuild;
        /// <summary>The files that can hold Unity's crash data of an earlier run. Null: none (editor, mobile, web, tests).</summary>
        public ICrashFiles CrashFiles;
    }

    /// <summary>The kind of a captured engine log message.</summary>
    public enum LogKind
    {
        Exception,
        Error,
        Assert,
        Warning,
    }
}
