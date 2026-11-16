using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UntitledAnalyticsSdk;
using Xunit;

namespace UntitledAnalytics.Tests
{
    sealed class FakeClock : IClock
    {
        public double Now = 1_800_000_000; // 2027-01-15
        public long Ms = 1_000_000;
        public double UnixSeconds => Now;
        public long Milliseconds => Ms;

        public void Advance(double seconds)
        {
            Now += seconds;
            Ms += (long)(seconds * 1000);
        }
    }

    sealed class MemoryStorage : IStorage
    {
        public readonly Dictionary<string, string> Blobs = new Dictionary<string, string>();
        /// <summary>Names whose read fails (the blob exists but cannot be read).</summary>
        public readonly HashSet<string> FailReads = new HashSet<string>();
        /// <summary>Names whose write and delete fail.</summary>
        public readonly HashSet<string> FailWrites = new HashSet<string>();
        public int Writes;

        public ReadStatus Read(string name, out string content)
        {
            content = null;
            if (FailReads.Contains(name)) return ReadStatus.Failed;
            return Blobs.TryGetValue(name, out content) ? ReadStatus.Ok : ReadStatus.Missing;
        }

        public bool Write(string name, string content)
        {
            if (FailWrites.Contains(name)) return false;
            Blobs[name] = content;
            Writes++;
            return true;
        }

        public bool Delete(string name)
        {
            if (FailWrites.Contains(name)) return false;
            Blobs.Remove(name);
            Writes++;
            return true;
        }
    }

    sealed class Request
    {
        public string Url;
        public IReadOnlyDictionary<string, string> Headers;
        public string Body;
        public Action<int> Complete;
        public List<JsonElement> Events => JsonDocument.Parse(Body).RootElement.GetProperty("events").EnumerateArray().ToList();
    }

    /// <summary>Records each request. Answers with the next queued status at once, or keeps it pending when Manual.</summary>
    sealed class FakeTransport : ITransport
    {
        public readonly List<Request> Requests = new List<Request>();
        public readonly Queue<int> Statuses = new Queue<int>();
        public bool Manual;

        public void Send(string url, IReadOnlyDictionary<string, string> headers, string body, Action<int> onComplete)
        {
            var request = new Request { Url = url, Headers = headers, Body = body, Complete = onComplete };
            Requests.Add(request);
            if (!Manual) onComplete(Statuses.Count > 0 ? Statuses.Dequeue() : 202);
        }

        public List<JsonElement> AllEvents => Requests.SelectMany(r => r.Events).ToList();
        public List<string> Names => AllEvents.Select(e => e.TryGetProperty("event", out var n) ? n.GetString() : e.GetProperty("type").GetString()).ToList();
    }

    sealed class ListLog : ILog
    {
        public readonly List<string> Lines = new List<string>();
        public void Info(string message) => Lines.Add("info: " + message);
        public void Warning(string message) => Lines.Add("warning: " + message);
        public void Error(string message) => Lines.Add("error: " + message);
        public bool Has(string text) => Lines.Any(line => line.Contains(text));
    }

    /// <summary>One device: storage and clock survive between launches.</summary>
    sealed class Device
    {
        public const string Key = "ua_pk_Xq34DFmqwql9UbVhRvCnPASQmrMmmnP0rqp8NEcPtXu002rW9AS";
        public const string Endpoint = "https://collect.example.com";
        public readonly MemoryStorage Storage = new MemoryStorage();
        public readonly FakeClock Clock = new FakeClock();
        public FakeTransport Transport = new FakeTransport();
        public ListLog Log = new ListLog();
        public int NextId;
        public bool Sequential;

        public static UntitledAnalyticsOptions Options(Action<UntitledAnalyticsOptions> change = null)
        {
            var options = new UntitledAnalyticsOptions { PublishableKey = Key, Endpoint = Endpoint, Build = "571", Storefront = "steam", FlushIntervalSeconds = 10 };
            change?.Invoke(options);
            return options;
        }

        /// <summary>A new process on this device.</summary>
        public AnalyticsClient Launch(UntitledAnalyticsOptions options = null, bool init = true)
        {
            Transport = new FakeTransport();
            Log = new ListLog();
            var client = new AnalyticsClient(options ?? Options(), new AppEnvironment { Platform = "windows", Locale = "en_US", DefaultAppVersion = "1.4.0" }, Storage, Transport, Clock, Log)
            {
                Random = () => 0.5,
            };
            if (Sequential) client.NewId = () => $"00000000-0000-4000-8000-{++NextId:D12}";
            if (init) Assert.True(client.Init(), string.Join("\n", Log.Lines));
            return client;
        }
    }

    static class Assert2
    {
        public static string Str(this JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        public static JsonElement Props(this JsonElement e) => e.GetProperty("properties");
        public static string Name(this JsonElement e) => e.Str("event") ?? e.Str("type");
    }
}
