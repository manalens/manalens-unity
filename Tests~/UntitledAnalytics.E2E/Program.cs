// One "game launch" against a real collector. Each mode is one process, so a
// mode can end without OnQuit() to act as a crash. Prints one line:
//   UA_RESULT {"ok":true,"anonymous_id":"...","failures":[]}
//
//   dotnet run --project "sdks/unity/Tests~/UntitledAnalytics.E2E" -- \
//     --endpoint=http://127.0.0.1:3293 --publishable-key=ua_pk_... --mode=offline \
//     --run-id=unity-1 --data-dir=/tmp/ua-unity/offline
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UntitledAnalyticsSdk;

static class Program
{
    sealed class HttpTransport : ITransport
    {
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        public readonly List<int> Statuses = new List<int>();

        public void Send(string url, IReadOnlyDictionary<string, string> headers, string body, Action<int, string> onComplete)
        {
            Task.Run(async () =>
            {
                var status = 0;
                string text = null;
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    foreach (var header in headers)
                        if (header.Key != "Content-Type") request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    using var response = await Http.SendAsync(request);
                    status = (int)response.StatusCode;
                    if (status >= 300)
                    {
                        text = await response.Content.ReadAsStringAsync();
                        Console.Error.WriteLine($"HTTP {status}: {text}");
                    }
                }
                catch (Exception)
                {
                    status = 0;
                }
                lock (Statuses) Statuses.Add(status);
                onComplete(status, text);
            });
        }
    }

    sealed class ConsoleLog : ILog
    {
        public readonly List<string> Lines = new List<string>();
        void Add(string line) { lock (Lines) Lines.Add(line); Console.Error.WriteLine("UntitledAnalytics: " + line); }
        public void Info(string message) => Add("info " + message);
        public void Warning(string message) => Add("warning " + message);
        public void Error(string message) => Add("error " + message);
    }

    static readonly List<string> Failures = new List<string>();
    static void Check(bool condition, string message) { if (!condition) Failures.Add(message); }

    static int Main(string[] args)
    {
        var arg = args.Where(a => a.StartsWith("--")).Select(a => a.Substring(2).Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "true");
        var mode = arg["mode"];
        var runId = arg["run-id"];
        var dataDir = arg["data-dir"];
        var endpoint = arg["endpoint"];
        var key = arg["publishable-key"];
        var result = new Dictionary<string, object> { ["mode"] = mode };
        var transport = new HttpTransport();
        var log = new ConsoleLog();
        var env = new AppEnvironment { Platform = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux", Locale = "en-US", DefaultAppVersion = "0.1.0-test" };
        AnalyticsClient Make(Action<UntitledAnalyticsOptions> change = null)
        {
            var options = new UntitledAnalyticsOptions { PublishableKey = key, Endpoint = endpoint, Build = "unity-test", Storefront = "itch", Debug = false };
            change?.Invoke(options);
            return new AnalyticsClient(options, env, new FileStorage(dataDir, log), transport, new SystemClock(), log);
        }
        Dictionary<string, object> Props(params (string, object)[] values)
        {
            var props = values.ToDictionary(v => v.Item1, v => v.Item2);
            props["run_id"] = runId;
            return props;
        }
        // Update() every 16 ms (a frame) until the queue is sent or the time is up.
        void Pump(AnalyticsClient client, double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                client.Update();
                if (client.QueueSize == 0 && !client.IsSending) return;
                Thread.Sleep(16);
            }
        }
        // As the Unity driver's ShutdownRoutine, then Application.quitting.
        void Shutdown(AnalyticsClient client, double seconds = 5)
        {
            client.BeginShutdown();
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until && !client.IsIdle) { client.Update(); Thread.Sleep(16); }
            client.OnQuit();
        }

        switch (mode)
        {
            case "offline":
            {
                // Queue against a dead endpoint, then send to the real one.
                var client = Make(o => o.Endpoint = "http://127.0.0.1:9");
                Check(client.Init(), "init");
                client.Track("level_start", Props(("level", 1)));
                Thread.Sleep(1100); // Keep the original time: received_at is later.
                client.Track("level_complete", Props(("level", 1), ("score", 1200), ("ratio", 0.75), ("perfect", true)));
                client.Track("resource", Props(("flow", "source"), ("currency", "gold"), ("amount", 25), ("item", "chest"), ("nested", new Dictionary<string, object> { ["a"] = 1 })));
                client.Identify($"player-{runId}", new Dictionary<string, object> { ["plan"] = "supporter" });
                Pump(client, 1.5);
                Check(client.QueueSize == 6, $"offline queue holds 6 events (has {client.QueueSize})");
                Check(transport.Statuses.All(s => s == 0), "dead endpoint gives network errors");
                client.SetEndpoint(endpoint);
                client.Track("test_done", Props());
                Shutdown(client);
                Check(client.QueueSize == 0, $"queue sent (left {client.QueueSize})");
                result["anonymous_id"] = client.AnonymousId;
                break;
            }
            case "consent":
            {
                var client = Make(o => o.ConsentRequired = true);
                Check(client.Init(), "init");
                client.Track("before_consent", Props());
                client.Identify($"consent-{runId}");
                Pump(client, 0.5);
                Check(transport.Statuses.Count == 0, "nothing sent before consent");
                Check(!File.Exists(Path.Combine(dataDir, AnalyticsClient.QueueName + ".txt")), "no queue file before consent");
                client.SetConsent(true);
                client.Track("after_consent", Props());
                Shutdown(client);
                result["anonymous_id"] = client.AnonymousId;
                break;
            }
            case "crash":
            {
                // Same device as "consent". Plays for a while, then the process dies: no OnQuit().
                var client = Make(o => { o.ConsentRequired = true; o.FlushIntervalSeconds = 1; o.Build = "unity-crash"; });
                Check(client.Init(), "init");
                Check(client.IsActive, "consent kept from the last launch");
                client.Track("before_crash", Props());
                Pump(client, 2);
                var until = DateTime.UtcNow.AddSeconds(2.2);
                while (DateTime.UtcNow < until) { client.Update(); Thread.Sleep(16); } // Ticks save the session.
                result["anonymous_id"] = client.AnonymousId;
                result["crashed_session"] = client.SessionId;
                break;
            }
            case "recovery":
            {
                var client = Make(o => o.ConsentRequired = true);
                Check(client.Init(), "init");
                result["anonymous_id"] = client.AnonymousId;
                result["session"] = client.SessionId;
                Shutdown(client);
                break;
            }
            case "withdraw":
            {
                // Same device as "consent": the player withdraws consent, then the state file is cut off.
                var client = Make(o => o.ConsentRequired = true);
                Check(client.Init(), "init");
                client.SetConsent(false);
                client.Track("after_withdraw", Props());
                Pump(client, 0.5);
                Check(File.Exists(Path.Combine(dataDir, AnalyticsClient.OptOutName + ".txt")), "opt-out marker written");
                var statePath = Path.Combine(dataDir, AnalyticsClient.StateName + ".txt");
                var text = File.ReadAllText(statePath);
                File.WriteAllText(statePath, text.Substring(0, text.Length / 2));
                File.Delete(Path.Combine(dataDir, AnalyticsClient.OptOutName + ".txt"));
                var restarted = Make(o => o.ConsentRequired = false);
                Check(restarted.Init(), "init after damage");
                restarted.Track("after_damage", Props());
                Pump(restarted, 0.5);
                Check(!restarted.IsActive, "a damaged state after an opt-out keeps the SDK off");
                Check(File.ReadAllText(statePath) == text.Substring(0, text.Length / 2), "the damaged state is not overwritten");
                Check(transport.Statuses.Count == 0, "nothing sent after the opt-out");
                break;
            }
            case "errors":
            {
                var client = Make(o => o.BeforeSend = props => (string)props["message"] == "secret error" ? null : props);
                Check(client.Init(), "init");
                client.Track("level_start", Props(("level", 3)));
                client.TrackError("Save failed for slot 3", new TrackErrorOptions { Severity = "critical", ErrorType = "SaveFailed", Handled = false, Properties = Props(("level", 3)) });
                client.TrackError("Save failed for slot 4", new TrackErrorOptions { Severity = "critical", ErrorType = "SaveFailed" }); // Same fingerprint within a second: dropped.
                client.TrackError("secret error"); // BeforeSend drops it.
                var thread = new Thread(() => client.CaptureLog("NullReferenceException: Object reference not set to an instance of an object",
                    "Game.Wave.Spawn () (at Assets/Scripts/Wave.cs:77)\nGame.Wave.Update () (at Assets/Scripts/Wave.cs:20)", LogKind.Exception));
                thread.Start();
                thread.Join();
                client.CaptureLog("Low memory", "", LogKind.Warning); // Warnings are off by default.
                Pump(client, 1);
                Shutdown(client);
                result["anonymous_id"] = client.AnonymousId;
                break;
            }
            case "disabled":
            {
                var client = Make(o => o.Enabled = false);
                Check(client.Init(), "init");
                client.Track("never_sent", Props());
                client.TrackError("never sent");
                Shutdown(client);
                Check(transport.Statuses.Count == 0, "disabled sends nothing");
                Check(!Directory.Exists(dataDir) || Directory.GetFiles(dataDir).Length == 0, "disabled writes no files");
                break;
            }
            case "refused":
            {
                var client = Make(o => o.PublishableKey = arg["secret-key"]);
                Check(!client.Init(), "a secret key is refused");
                Check(log.Lines.Any(l => l.Contains("secret key")), "the refusal names the secret key");
                client.Track("never_sent", Props());
                Pump(client, 0.3);
                Check(transport.Statuses.Count == 0, "a refused key sends nothing");
                break;
            }
            default:
                Failures.Add("unknown mode " + mode);
                break;
        }
        lock (transport.Statuses) result["statuses"] = transport.Statuses.ToArray();
        result["failures"] = Failures;
        result["ok"] = Failures.Count == 0;
        Console.WriteLine("UA_RESULT " + JsonSerializer.Serialize(result));
        return Failures.Count == 0 ? 0 : 1;
    }
}
