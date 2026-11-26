using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using ManalensSdk;
using Xunit;

namespace Manalens.Tests
{
    public class InitTests
    {
        [Theory]
        [InlineData("mana_sk_ERSa1DIPSRTfHDgKb3J1ZquVsyJjGwhP25biXByyey8003IFfdW", "secret key")]
        [InlineData("mana_api_IDjT80ncb5T2xm2I3nVPEqnbM1YPSUOZ8uZf04QDDnU001oS2MK", "API key")]
        [InlineData("ua_cccccccccccccccccccccccccccccccc", "must start with mana_pk_")]
        [InlineData("mana_pk_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "malformed")]
        [InlineData("", "required")]
        public void RefusesKeysThatAreNotPublishable(string key, string reason)
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.PublishableKey = key), init: false);
            Assert.False(client.Init());
            Assert.True(device.Log.Has(reason), string.Join("\n", device.Log.Lines));
            Assert.False(client.IsActive);
            client.Track("x");
            client.Update();
            Assert.Empty(device.Transport.Requests);
            Assert.Empty(device.Storage.Blobs);
        }

        [Fact]
        public void AcceptsOnlyAPublishableKeyWithAChecksum()
        {
            Assert.Null(Keys.PublishableKeyRefusal(Device.Key));
            Assert.NotNull(Keys.PublishableKeyRefusal("ua_" + new string('c', 32)));
            // The checksum is the same as packages/shared/src/keys.ts (CRC32, base62).
            Assert.Equal("0014X8oL", Keys.Checksum(Device.Key.Substring(0, Device.Key.Length - 8)));
            Assert.NotNull(Keys.PublishableKeyRefusal(Device.Key.Substring(0, Device.Key.Length - 1) + "T"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("ftp://collect.example.com")]
        [InlineData("collect.example.com")]
        public void RefusesAMissingOrNonHttpEndpoint(string endpoint)
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.Endpoint = endpoint), init: false);
            Assert.False(client.Init());
        }

        [Fact]
        public void DisabledSendsNothingAndWritesNothing()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.Enabled = false));
            Assert.True(client.IsDisabled);
            client.Track("level_start");
            client.Identify("player");
            client.TrackError("boom");
            client.CaptureLog("NullReferenceException: x", "", LogKind.Exception);
            client.SetConsent(true);
            client.SendNow();
            client.Update();
            client.OnFocusChanged(false);
            client.BeginShutdown();
            client.OnQuit();
            Assert.Empty(device.Transport.Requests);
            Assert.Empty(device.Storage.Blobs);
            Assert.Equal(0, device.Storage.Writes);
            Assert.Equal("", client.AnonymousId);
            Assert.Equal("https://x.example/", client.LinkUrl("https://x.example/"));
            Assert.DoesNotContain(device.Log.Lines, line => line.StartsWith("warning") || line.StartsWith("error"));
        }

        [Fact]
        public void BadLabelsAndStorefrontAreIgnoredWithAWarning()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => { o.Build = "bad/build"; o.Storefront = "switch"; o.AppVersion = new string('1', 33); }));
            client.Update();
            var app = device.Transport.AllEvents[0].GetProperty("context").GetProperty("app");
            Assert.False(app.TryGetProperty("build", out _));
            Assert.False(app.TryGetProperty("storefront", out _));
            Assert.False(app.TryGetProperty("app_version", out _));
            Assert.True(device.Log.Has("Build must be"));
            Assert.True(device.Log.Has("Storefront must be one of"));
        }

        [Theory]
        [InlineData("psn")]
        [InlineData("xbox")]
        [InlineData("nintendo")]
        public void ConsoleStorefrontsAreSent(string storefront)
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.Storefront = storefront));
            client.Update();
            Assert.Equal(storefront, device.Transport.AllEvents[0].GetProperty("context").GetProperty("app").Str("storefront"));
            Assert.False(device.Log.Has("Storefront must be one of"));
        }

        [Fact]
        public void PlatformOptionNamesAConsoleBuild()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.Platform = "switch2"));
            client.Update();
            Assert.All(device.Transport.AllEvents, e => Assert.Equal("switch2", e.GetProperty("context").GetProperty("app").Str("platform")));
        }

        [Fact]
        public void AWrongPlatformOptionKeepsTheDetectedPlatform()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.Platform = "ps4"));
            client.Update();
            Assert.Equal("windows", device.Transport.AllEvents[0].GetProperty("context").GetProperty("app").Str("platform"));
            Assert.True(device.Log.Has("Platform must be one of"));
        }

        [Fact]
        public void AppVersionDefaultsToTheEngineVersion()
        {
            var device = new Device();
            var client = device.Launch();
            client.Update();
            Assert.Equal("1.4.0", device.Transport.AllEvents[0].GetProperty("context").GetProperty("app").Str("app_version"));
        }
    }

    public class EnvelopeTests
    {
        [Theory]
        [InlineData(480, true)]
        [InlineData(-300, true)]
        [InlineData(0, true)]
        [InlineData(841, false)]
        [InlineData(-721, false)]
        public void UtcOffsetIsSentOnlyInTheAcceptedRange(int minutes, bool sent)
        {
            var device = new Device { UtcOffsetMinutes = minutes };
            var client = device.Launch();
            client.Update();
            var context = device.Transport.Requests[0].Events[0].GetProperty("context");
            Assert.Equal(sent, context.TryGetProperty("utc_offset", out var value));
            if (sent) Assert.Equal(minutes, value.GetInt32());
        }

        [Fact]
        public void FirstLaunchSendsSessionStartAndGameOpenInTheAppEnvelope()
        {
            var device = new Device();
            var client = device.Launch();
            client.Update();
            Assert.Single(device.Transport.Requests);
            var request = device.Transport.Requests[0];
            Assert.Equal("https://collect.example.com/v0/events", request.Url);
            Assert.Equal(Device.Key, request.Headers["x-publishable-key"]);
            Assert.Equal("application/json", request.Headers["Content-Type"]);
            Assert.Equal(new[] { "session_start", "game_open" }, device.Transport.Names);
            foreach (var e in request.Events)
            {
                Assert.Equal("track", e.Str("type"));
                Assert.Equal("app", e.Str("channel"));
                Assert.Equal("0.1.6", e.Str("version"));
                Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", e.Str("message_id"));
                Assert.Equal(client.AnonymousId, e.Str("anonymous_id"));
                Assert.Equal(client.SessionId, e.Str("session_id"));
                Assert.Equal("2027-01-15T08:00:00.000Z", e.Str("original_timestamp"));
                var context = e.GetProperty("context");
                Assert.Equal(new[] { "app", "library_name", "library_version", "locale" }, context.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
                Assert.Equal("en-US", context.Str("locale"));
                Assert.Equal("Manalens Unity SDK", context.Str("library_name"));
                var app = context.GetProperty("app");
                Assert.Equal("windows", app.Str("platform"));
                Assert.Equal("unity", app.Str("engine"));
                Assert.Equal("571", app.Str("build"));
                Assert.Equal("steam", app.Str("storefront"));
                Assert.False(e.TryGetProperty("user_id", out _));
            }
            var open = request.Events[1].Props();
            Assert.Equal(JsonValueKind.True, open.GetProperty("is_first_open").ValueKind);
            Assert.Equal("2027-01-15T08:00:00.000Z", open.Str("first_open_at"));
        }

        [Fact]
        public void SecondLaunchKeepsTheAnonymousIdAndIsNotAFirstOpen()
        {
            var device = new Device();
            var first = device.Launch();
            first.Update();
            first.OnQuit();
            var id = first.AnonymousId;
            device.Clock.Advance(3600);
            var second = device.Launch();
            second.Update();
            Assert.Equal(id, second.AnonymousId);
            var open = device.Transport.AllEvents.First(e => e.Name() == "game_open").Props();
            Assert.Equal(JsonValueKind.False, open.GetProperty("is_first_open").ValueKind);
            Assert.Equal("2027-01-15T08:00:00.000Z", open.Str("first_open_at"));
            Assert.DoesNotContain("app_unclean_exit", device.Transport.Names);
        }

        [Fact]
        public void TrackKeepsScalarsWithTheirJsonTypes()
        {
            var device = new Device();
            var client = device.Launch();
            client.Track("resource", new Dictionary<string, object>
            {
                ["int"] = 3,
                ["long"] = 9_000_000_000L,
                ["float"] = 0.1f,
                ["double"] = 1.5,
                ["decimal"] = 2.25m,
                ["bool"] = true,
                ["null"] = null,
                ["text"] = new string('x', 2000),
                ["enum"] = DayOfWeek.Friday,
                ["nested"] = new Dictionary<string, object> { ["a"] = 1 },
                ["array"] = new[] { 1, 2 },
                ["nan"] = double.NaN,
                ["inf"] = float.PositiveInfinity,
                [new string('k', 101)] = 1,
                ["quote"] = "a\"b\\c\nd\u0001",
            });
            client.Update();
            var props = device.Transport.AllEvents.Single(e => e.Name() == "resource").Props();
            Assert.Equal(3, props.GetProperty("int").GetInt32());
            Assert.Equal(9_000_000_000L, props.GetProperty("long").GetInt64());
            Assert.Equal("0.1", props.GetProperty("float").GetRawText());
            Assert.Equal(1.5, props.GetProperty("double").GetDouble());
            Assert.Equal("2.25", props.GetProperty("decimal").GetRawText());
            Assert.Equal(JsonValueKind.True, props.GetProperty("bool").ValueKind);
            Assert.Equal(JsonValueKind.Null, props.GetProperty("null").ValueKind);
            Assert.Equal(1024, props.Str("text").Length);
            Assert.Equal("Friday", props.Str("enum"));
            Assert.Equal("a\"b\\c\nd\u0001", props.Str("quote"));
            foreach (var dropped in new[] { "nested", "array", "nan", "inf", new string('k', 101) }) Assert.False(props.TryGetProperty(dropped, out _), dropped);
            Assert.True(device.Log.Has("property nested is not a scalar"));
        }

        [Fact]
        public void PropertiesAreTrimmedToTheCollectorLimits()
        {
            var device = new Device();
            var client = device.Launch();
            var many = new Dictionary<string, object>();
            for (var i = 0; i < 60; i++) many["k" + i] = i;
            client.Track("many_keys", many);
            var big = new Dictionary<string, object>();
            for (var i = 0; i < 10; i++) big["k" + i] = new string('€', 1024);
            client.Track("big_values", big);
            client.Update();
            var manyProps = device.Transport.AllEvents.Single(e => e.Name() == "many_keys").Props();
            Assert.Equal(50, manyProps.EnumerateObject().Count());
            Assert.True(manyProps.TryGetProperty("k49", out _));
            Assert.False(manyProps.TryGetProperty("k50", out _));
            var bigProps = device.Transport.AllEvents.Single(e => e.Name() == "big_values").Props();
            Assert.Equal(2, bigProps.EnumerateObject().Count()); // 3 KB of UTF-8 for each value
            Assert.True(Encoding.UTF8.GetByteCount(bigProps.GetRawText()) <= AnalyticsClient.MaxPropertyBytes);
            Assert.True(device.Log.Has("properties over the limit"));
        }

        [Fact]
        public void NumbersUseTheInvariantCultureUnderAGermanLocale()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                var device = new Device();
                var client = device.Launch();
                client.Track("score", new Dictionary<string, object> { ["ratio"] = 1.5, ["f"] = 2.5f, ["m"] = 3.5m });
                client.Update();
                var raw = device.Transport.Requests[0].Body;
                Assert.Contains("\"ratio\":1.5", raw);
                Assert.Contains("\"f\":2.5", raw);
                Assert.Contains("\"m\":3.5", raw);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void TrackRefusesAnEmptyOrLongName(string name)
        {
            var device = new Device();
            var client = device.Launch();
            client.Track(name);
            client.Track(new string('e', 129));
            client.Track(new string('e', 128));
            client.Update();
            Assert.Equal(3, device.Transport.AllEvents.Count);
            Assert.True(device.Log.Has("event name must be 1 to 128 characters"));
        }

        [Fact]
        public void IdentifyAddsTheUserIdAndTraitsAndResetForgetsThePlayer()
        {
            var device = new Device();
            var client = device.Launch();
            var firstAnon = client.AnonymousId;
            client.Identify("player_42", new Dictionary<string, object> { ["plan"] = "supporter", ["nested"] = new object() });
            client.Track("after_identify");
            client.Reset();
            client.Track("after_reset");
            client.Update();
            client.Update();
            var events = device.Transport.AllEvents;
            var identify = events.Single(e => e.Str("type") == "identify");
            Assert.Equal("player_42", identify.Str("user_id"));
            Assert.False(identify.TryGetProperty("event", out _));
            Assert.Equal("supporter", identify.GetProperty("traits").Str("plan"));
            Assert.False(identify.GetProperty("traits").TryGetProperty("nested", out _));
            Assert.Equal("player_42", events.Single(e => e.Name() == "after_identify").Str("user_id"));
            var afterReset = events.Single(e => e.Name() == "after_reset");
            Assert.Null(afterReset.Str("user_id"));
            Assert.NotEqual(firstAnon, afterReset.Str("anonymous_id"));
            Assert.Equal(new[] { "session_start", "game_open", "identify", "after_identify", "session_end", "session_start", "after_reset" }, device.Transport.Names);
            // The user ID survives a restart; a reset clears it for good.
            var next = device.Launch();
            Assert.Equal("", next.UserId);
        }

        [Fact]
        public void LinkUrlAddsTheHandoffParameterAndKeepsTheFragment()
        {
            var device = new Device();
            var client = device.Launch();
            var id = client.AnonymousId;
            Assert.Equal($"https://example.com/wiki?mana_hid={id}.1800000000", client.LinkUrl("https://example.com/wiki"));
            Assert.Equal($"https://example.com/wiki?a=1&mana_hid={id}.1800000000#top", client.LinkUrl("https://example.com/wiki?a=1#top"));
            Assert.Equal($"https://example.com/?mana_hid={id}.1800000000", client.LinkUrl("https://example.com/?"));
        }
    }

    public class QueueTests
    {
        [Fact]
        public void BatchesAtFlushAtAndClampsTheBatchTo50()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.FlushAt = 500));
            device.Transport.Manual = true;
            client.Update();
            device.Transport.Requests[0].Complete(202);
            for (var i = 0; i < 120; i++) client.Track("e" + i);
            client.Update();
            Assert.Equal(50, device.Transport.Requests[1].Events.Count);
            device.Transport.Requests[1].Complete(202);
            client.Update();
            Assert.Equal(50, device.Transport.Requests[2].Events.Count);
            device.Transport.Requests[2].Complete(202);
            Assert.Equal(20, client.QueueSize);
        }

        [Fact]
        public void SendsOnlyOneBatchAtATime()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.FlushAt = 2));
            device.Transport.Manual = true;
            client.Update();
            client.Track("a");
            client.Track("b");
            client.Update();
            client.SendNow();
            Assert.Single(device.Transport.Requests);
            Assert.True(client.IsSending);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(500)]
        [InlineData(503)]
        [InlineData(408)]
        [InlineData(429)]
        public void KeepsTheBatchAndBacksOffAfterARetryableFailure(int status)
        {
            var device = new Device();
            var client = device.Launch();
            var flushed = new List<(int, bool)>();
            client.Flushed += (sent, ok) => flushed.Add((sent, ok));
            device.Transport.Statuses.Enqueue(status);
            client.Update();
            Assert.Equal(2, client.QueueSize);
            Assert.Equal((0, false), flushed.Single());
            // Backoff 2 s with jitter 0.5 to 1 (Random = 0.5: 1.5 s).
            client.SendNow();
            Assert.Single(device.Transport.Requests);
            device.Clock.Advance(1.6);
            client.Update();
            Assert.Equal(2, device.Transport.Requests.Count);
            Assert.Equal(device.Transport.Requests[0].Events.Select(e => e.Str("message_id")), device.Transport.Requests[1].Events.Select(e => e.Str("message_id")));
            Assert.Equal(0, client.QueueSize);
            Assert.Equal((2, true), flushed.Last());
        }

        [Fact]
        public void EachSendCarriesTheDeviceClockOfThatSendAsSentAt()
        {
            // P-2799: the collector corrects event times with sent_at, so a retry sends its own clock time.
            var device = new Device();
            var client = device.Launch();
            device.Transport.Statuses.Enqueue(503);
            client.Update();
            client.SendNow();
            device.Clock.Advance(1.6);
            client.Update();
            var sentAt = device.Transport.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("sent_at").GetString()).ToList();
            Assert.Equal("2027-01-15T08:00:00.000Z", sentAt[0]);
            Assert.Equal("2027-01-15T08:00:01.600Z", sentAt[1]);
            Assert.Equal("2027-01-15T08:00:00.000Z", device.Transport.Requests[1].Events[0].Str("original_timestamp"));
        }

        [Fact]
        public void BackoffDoublesWithJitterUpToFiveMinutes()
        {
            var device = new Device();
            var client = device.Launch();
            client.Random = () => 0.999;
            var waits = new List<double>();
            for (var i = 0; i < 12; i++)
            {
                device.Transport.Statuses.Enqueue(503);
                var before = device.Transport.Requests.Count;
                var start = device.Clock.Now;
                while (device.Transport.Requests.Count == before)
                {
                    device.Clock.Advance(0.25);
                    client.SendNow();
                }
                if (i > 0) waits.Add(device.Clock.Now - start);
            }
            // The wait before attempt n+1 is 2^n seconds times the jitter, at most 300 s.
            Assert.InRange(waits[0], 2 * 0.99, 2 + 0.25);
            Assert.InRange(waits[1], 4 * 0.99, 4 + 0.25);
            Assert.InRange(waits.Last(), 300 * 0.99, 300 + 0.25);
        }

        [Theory]
        [InlineData("120", 120)]
        [InlineData(" 60 ", 60)]
        [InlineData("0", 0)]
        [InlineData("99999", 99999)]
        [InlineData(null, 0)]
        [InlineData("", 0)]
        [InlineData("soon", 0)]
        [InlineData("-5", 0)]
        [InlineData("1.5", 0)]
        public void ParsesRetryAfterSeconds(string header, double expected)
        {
            Assert.Equal(expected, AnalyticsClient.ParseRetryAfter(header, 1_800_000_000));
        }

        [Fact]
        public void ParsesRetryAfterHttpDates()
        {
            var now = 1_800_000_000.0;
            var at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_090).ToString("r", CultureInfo.InvariantCulture);
            Assert.Equal(90, AnalyticsClient.ParseRetryAfter(at, now));
            var past = DateTimeOffset.FromUnixTimeSeconds(1_799_999_000).ToString("r", CultureInfo.InvariantCulture);
            Assert.Equal(0, AnalyticsClient.ParseRetryAfter(past, now));
            Assert.Equal(0, AnalyticsClient.ParseRetryAfter("Fri, 99 Nov 2026 00:00:00 GMT", now));
        }

        /// <summary>Seconds until the client sends the retry after one failed send with this status and Retry-After.</summary>
        static double WaitAfter(Device device, AnalyticsClient client, int status, string retryAfter)
        {
            device.Transport.Statuses.Enqueue(status);
            device.Transport.RetryAfter = retryAfter;
            client.Update();
            Assert.Single(device.Transport.Requests);
            device.Transport.RetryAfter = null;
            var start = device.Clock.Now;
            while (device.Transport.Requests.Count == 1)
            {
                Assert.True(device.Clock.Now - start < 7200, "no retry within 2 hours");
                device.Clock.Advance(0.25);
                client.SendNow();
            }
            return device.Clock.Now - start;
        }

        [Theory]
        [InlineData(429, "60", 60)]
        [InlineData(503, "30", 30)]
        [InlineData(500, "10", 10)]
        [InlineData(429, "99999", 3600)] // At most 1 hour.
        [InlineData(429, "1", 1.5)] // Shorter than the backoff: the backoff wins (2 s times jitter 0.75).
        [InlineData(429, null, 1.5)] // Missing: the backoff only.
        [InlineData(503, "later", 1.5)] // Invalid: the backoff only.
        public void WaitsAtLeastRetryAfterAtMostOneHour(int status, string retryAfter, double expected)
        {
            var device = new Device();
            var client = device.Launch();
            var wait = WaitAfter(device, client, status, retryAfter);
            Assert.InRange(wait, expected, expected + 0.25);
            Assert.Equal(0, client.QueueSize);
        }

        [Fact]
        public void WaitsUntilARetryAfterDate()
        {
            var device = new Device();
            var client = device.Launch();
            var at = DateTimeOffset.FromUnixTimeSeconds((long)device.Clock.Now + 120).ToString("r", CultureInfo.InvariantCulture);
            var wait = WaitAfter(device, client, 429, at);
            Assert.InRange(wait, 120, 120.25);
        }

        [Fact]
        public void IgnoresRetryAfterOnAnAcceptedOrRefusedBatch()
        {
            var device = new Device();
            var client = device.Launch();
            device.Transport.RetryAfter = "600";
            client.Update();
            Assert.Equal(0, client.QueueSize);
            client.Track("after");
            client.SendNow();
            Assert.Equal(2, device.Transport.Requests.Count);
        }

        [Fact]
        public void ACustomTransportWithOnlyITransportStillWorksWithTheBackoff()
        {
            var device = new Device { OnlyITransport = true };
            var client = device.Launch();
            // The inner fake has a Retry-After, but an ITransport-only transport cannot pass it on.
            var wait = WaitAfter(device, client, 429, "60");
            Assert.InRange(wait, 1.5, 1.75);
            Assert.Equal(0, client.QueueSize);
        }

        [Theory]
        [InlineData(400)]
        [InlineData(401)]
        [InlineData(413)]
        [InlineData(422)]
        public void DropsTheBatchOnOther4xxAndRaisesSendFailed(int status)
        {
            var device = new Device();
            var client = device.Launch();
            var failed = new List<(int, int)>();
            client.SendFailed += (s, n) => failed.Add((s, n));
            device.Transport.Statuses.Enqueue(status);
            client.Update();
            Assert.Equal(0, client.QueueSize);
            Assert.Equal((status, 2), failed.Single());
            Assert.True(device.Log.Has($"collector refused 2 events (HTTP {status})"));
        }

        [Fact]
        public void ARefusalLogsTheReasonOfTheCollectorAndIsNotSentAgain()
        {
            var device = new Device();
            var client = device.Launch();
            device.Transport.Statuses.Enqueue(400);
            device.Transport.ResponseBody = "{\"error\":{\"code\":\"INVALID_EVENT\",\"message\":\"1 of 2 events break the session ID contract. No event of the batch was accepted\",\"refused\":[{\"index\":1,\"message_id\":\"x\",\"code\":\"SESSION_ID_REQUIRED\",\"message\":\"An event with an anonymous_id needs a session_id\"}]}}";
            client.Update();
            Assert.Equal(0, client.QueueSize);
            Assert.True(device.Log.Has("collector refused 2 events (HTTP 400) INVALID_EVENT, SESSION_ID_REQUIRED: 1 of 2 events break the session ID contract"), string.Join("\n", device.Log.Lines));
            Assert.True(device.Log.Has("they are dropped"));
            // The refused batch is gone: the next send has none of its events.
            var sent = device.Transport.Requests.Count;
            device.Transport.ResponseBody = null;
            client.Track("after_refusal");
            client.SendNow();
            Assert.Equal(sent + 1, device.Transport.Requests.Count);
            Assert.Equal(new[] { "after_refusal" }, device.Transport.Requests.Last().Events.Select(e => e.Name()).ToArray());
            Assert.Equal("", AnalyticsClient.RefusalReason(null));
            Assert.Equal("", AnalyticsClient.RefusalReason("<html>Bad Gateway</html>"));
        }

        [Fact]
        public void RefusedEventsOfAnAcceptedBatchAreReportedAndNotSentAgain()
        {
            // P-2787: the collector stores the valid events of a batch and lists the others.
            var device = new Device();
            var client = device.Launch();
            var failed = new List<(int, int)>();
            var flushed = new List<(int, bool)>();
            client.SendFailed += (s, n) => failed.Add((s, n));
            client.Flushed += (sent, ok) => flushed.Add((sent, ok));
            device.Transport.Statuses.Enqueue(202);
            device.Transport.ResponseBody = "{\"accepted\":1,\"rejected\":1,\"errors\":[{\"index\":1,\"message_id\":\"x\",\"code\":\"INVALID_EVENT\",\"message\":\"original_timestamp: Invalid ISO datetime\"}]}";
            client.Update();
            Assert.Equal(0, client.QueueSize);
            Assert.Equal((202, 1), failed.Single());
            Assert.Equal((1, true), flushed.Last());
            Assert.True(device.Log.Has("collector refused 1 of 2 events (HTTP 202) INVALID_EVENT: original_timestamp: Invalid ISO datetime; they are dropped"), string.Join("\n", device.Log.Lines));
            Assert.Equal(2, AnalyticsClient.RejectedCount("{\"rejected\": 2}"));
            Assert.Equal(0, AnalyticsClient.RejectedCount("{\"accepted\":1}"));
            Assert.Equal(0, AnalyticsClient.RejectedCount(null));
        }

        [Fact]
        public void QueueOverMaxQueueDropsTheOldest()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => { o.MaxQueue = 30; o.FlushAt = 10; }));
            device.Transport.Statuses.Enqueue(0);
            client.Update(); // Offline: 2 events stay, the SDK backs off.
            var failed = new List<(int, int)>();
            client.SendFailed += (s, n) => failed.Add((s, n));
            for (var i = 0; i < 40; i++) client.Track("e" + i);
            Assert.Equal(30, client.QueueSize);
            Assert.Equal(12, failed.Sum(f => f.Item2));
            Assert.All(failed, f => Assert.Equal(0, f.Item1));
            // MaxQueue is at least FlushAt.
            var other = new Device();
            var small = other.Launch(Device.Options(o => { o.MaxQueue = 5; o.FlushAt = 10; }));
            for (var i = 0; i < 6; i++) small.Track("e" + i);
            Assert.Equal(8, small.QueueSize);
        }

        [Fact]
        public void TheQueueSurvivesARestartAndKeepsTheOriginalTime()
        {
            var device = new Device();
            var client = device.Launch();
            device.Transport.Statuses.Enqueue(0);
            client.Update(); // Offline.
            client.Track("offline_event");
            client.OnFocusChanged(false); // Saves the queue.
            var next = device.Launch(); // Killed and launched again, 0 s later.
            next.Update();
            var names = device.Transport.Names;
            Assert.Equal(new[] { "session_start", "game_open", "offline_event" }, names.Take(3));
            Assert.Equal("2027-01-15T08:00:00.000Z", device.Transport.AllEvents[2].Str("original_timestamp"));
            Assert.Equal(names.Count, names.Count(n => n != null));
        }

        [Fact]
        public void AFirstLaunchKilledOfflineBeforeAnySaveKeepsTheInstall()
        {
            var device = new Device();
            device.Launch(); // First launch, offline. Killed before any send, tick, focus loss or quit.
            Assert.Contains("game_open", device.Storage.Blobs[AnalyticsClient.QueueName]);
            device.Clock.Advance(3600);
            var next = device.Launch();
            next.Update();
            var opens = device.Transport.AllEvents.Where(e => e.Name() == "game_open").ToList();
            Assert.Equal(2, opens.Count);
            Assert.True(opens[0].Props().GetProperty("is_first_open").GetBoolean());
            Assert.Equal("2027-01-15T08:00:00.000Z", opens[0].Str("original_timestamp"));
            Assert.False(opens[1].Props().GetProperty("is_first_open").GetBoolean());
            Assert.Equal(opens[0].Str("anonymous_id"), opens[1].Str("anonymous_id"));
        }

        [Fact]
        public void ATickWhileShuttingDownStartsNoNewSession()
        {
            var device = new Device();
            var client = device.Launch();
            device.Transport.Statuses.Enqueue(0); // The first send fails, so the shutdown waits.
            client.BeginShutdown();
            device.Clock.Advance(11); // Past the 10 s tick, still waiting.
            client.Update();
            client.Update();
            var names = device.Transport.Names;
            Assert.Equal("session_end", names.Last());
            var starts = device.Transport.AllEvents.Where(e => e.Name() == "session_start").Select(e => e.Str("message_id")).Distinct();
            Assert.Single(starts); // A failed batch is sent again with the same message IDs.
        }

        [Fact]
        public void FocusGainedWhileShuttingDownStartsNoNewSession()
        {
            var device = new Device();
            var client = device.Launch();
            client.BeginShutdown();
            client.OnFocusChanged(true); // Unity sends focus on the first frames, also after an early Shutdown().
            Assert.Equal("", client.SessionId);
            client.Update();
            var starts = device.Transport.AllEvents.Where(e => e.Name() == "session_start").Select(e => e.Str("message_id")).Distinct();
            Assert.Single(starts);
            Assert.Equal("session_end", device.Transport.Names.Last());
        }

        [Fact]
        public void TheUncleanExitIsSavedBeforeAnySend()
        {
            var device = new Device();
            var client = device.Launch();
            client.Update();
            device.Clock.Advance(60);
            device.Launch(); // The first process was killed; this one is killed too before any save.
            Assert.Contains("app_unclean_exit", device.Storage.Blobs[AnalyticsClient.QueueName]);
        }

        [Fact]
        public void OfflineEventsSentDaysLaterKeepTheTimeTheyHappened()
        {
            var device = new Device();
            var client = device.Launch();
            device.Transport.Statuses.Enqueue(0);
            client.Update(); // Offline.
            device.Clock.Advance(90);
            client.Track("offline_event");
            client.OnQuit();
            device.Clock.Advance(3 * 24 * 3600); // Launched again 3 days later, online.
            var next = device.Launch();
            next.Update();
            var sent = device.Transport.AllEvents.Single(e => e.Name() == "offline_event");
            Assert.Equal("2027-01-15T08:01:30.000Z", sent.Str("original_timestamp"));
        }

        [Fact]
        public void EventsOlderThanSevenDaysAreDroppedOnLoad()
        {
            var device = new Device();
            var client = device.Launch();
            device.Transport.Statuses.Enqueue(0);
            client.Update();
            client.OnQuit();
            device.Clock.Advance(8 * 24 * 3600);
            var next = device.Launch(init: false);
            var failed = new List<(int, int)>();
            next.SendFailed += (s, n) => failed.Add((s, n));
            Assert.True(next.Init());
            next.Update();
            Assert.Equal((0, 3), failed.Single()); // session_start, game_open, session_end
            Assert.Equal(new[] { "session_start", "game_open" }, device.Transport.Names);
        }

        [Fact]
        public void QueuedEventsAreSentWithTheKeyOfTheLaunchThatSendsThem()
        {
            // The disk queue does not store the key (G-2530 question). A dev
            // build's unsent events go to the release key's project.
            const string releaseBody = "mana_pk_ReleaseKeyReleaseKeyReleaseKeyReleaseKey000";
            var releaseKey = releaseBody + Keys.Checksum(releaseBody);
            var device = new Device();
            var dev = device.Launch(Device.Options(o => o.Build = "dev"));
            device.Transport.Statuses.Enqueue(0);
            dev.Update();
            dev.Track("dev_only");
            dev.OnQuit();
            var release = device.Launch(Device.Options(o => { o.PublishableKey = releaseKey; o.Build = "1"; }));
            release.Update();
            Assert.All(device.Transport.Requests, r => Assert.Equal(releaseKey, r.Headers["x-publishable-key"]));
            var devEvent = device.Transport.AllEvents.Single(e => e.Name() == "dev_only");
            Assert.Equal("dev", devEvent.GetProperty("context").GetProperty("app").Str("build"));
        }

        [Fact]
        public void IsIdleAfterAFailureSoShutdownStopsWaiting()
        {
            var device = new Device();
            var client = device.Launch();
            device.Transport.Statuses.Enqueue(0);
            device.Transport.Statuses.Enqueue(0);
            client.Update();
            client.BeginShutdown();
            Assert.True(client.IsIdle);
            Assert.Equal(3, client.QueueSize);
        }
    }

    public class SessionTests
    {
        [Fact]
        public void TheSessionRotatesAfter30MinutesWithoutFocusOrEvents()
        {
            var device = new Device();
            var client = device.Launch();
            var first = client.SessionId;
            client.OnFocusChanged(false);
            device.Clock.Advance(31 * 60);
            client.Update();
            client.OnFocusChanged(true);
            client.SendNow();
            Assert.NotEqual(first, client.SessionId);
            var events = device.Transport.AllEvents;
            var end = events.Single(e => e.Name() == "session_end");
            Assert.Equal(first, end.Str("session_id"));
            Assert.Equal(0, end.Props().GetProperty("duration_s").GetInt64());
            Assert.Equal(client.SessionId, events.Last(e => e.Name() == "session_start").Str("session_id"));
        }

        [Fact]
        public void AFocusedGameStartsANewSessionWhenTheSessionIs24HoursOld()
        {
            var device = new Device();
            var client = device.Launch();
            var first = client.SessionId;
            // A tick every 10 minutes for 23 hours and 50 minutes: one session.
            for (var i = 0; i < 143; i++)
            {
                device.Clock.Advance(600);
                client.Update();
            }
            Assert.Equal(first, client.SessionId);
            device.Clock.Advance(600);
            client.Update();
            var second = client.SessionId;
            Assert.NotEqual(first, second);
            client.Track("after_a_day");
            client.SendNow();
            var events = device.Transport.AllEvents;
            var end = events.Single(e => e.Name() == "session_end");
            Assert.Equal(first, end.Str("session_id"));
            Assert.Equal(86400, end.Props().GetProperty("duration_s").GetInt64());
            Assert.Equal(2, events.Count(e => e.Name() == "session_start"));
            Assert.Equal(second, events.Single(e => e.Name() == "after_a_day").Str("session_id"));
            // Every event has a session ID, and it is never the anonymous ID (the collector refuses both).
            Assert.All(events, e => Assert.False(string.IsNullOrEmpty(e.Str("session_id"))));
            Assert.All(events, e => Assert.NotEqual(e.Str("anonymous_id"), e.Str("session_id")));
        }

        [Fact]
        public void FocusKeepsTheSessionAlive()
        {
            var device = new Device();
            var client = device.Launch();
            var first = client.SessionId;
            for (var i = 0; i < 400; i++)
            {
                device.Clock.Advance(10);
                client.Update();
            }
            client.Track("still_here");
            Assert.Equal(first, client.SessionId);
            client.BeginShutdown();
            client.Update();
            var end = device.Transport.AllEvents.Single(e => e.Name() == "session_end");
            Assert.Equal(4000, end.Props().GetProperty("duration_s").GetInt64());
        }

        [Fact]
        public void ShutdownEndsTheSessionAndSendsIt()
        {
            var device = new Device();
            var client = device.Launch();
            device.Clock.Advance(90.4);
            client.BeginShutdown();
            Assert.True(client.IsIdle);
            Assert.Equal(new[] { "session_start", "game_open", "session_end" }, device.Transport.Names);
            Assert.Equal(90, device.Transport.AllEvents[2].Props().GetProperty("duration_s").GetInt64());
            Assert.Equal("", client.SessionId);
        }

        [Fact]
        public void ACrashSendsAppUncleanExitInTheCrashedSessionAtTheNextLaunch()
        {
            var device = new Device();
            var crashed = device.Launch();
            crashed.Update();
            var crashedSession = crashed.SessionId;
            device.Clock.Advance(25);
            crashed.Update(); // The 10 s tick runs at +25 s: it keeps the focused session alive and saves it.
            // No OnQuit: the process died.
            device.Clock.Advance(600);
            var next = device.Launch(Device.Options(o => o.Build = "572"));
            next.Update();
            var events = device.Transport.AllEvents;
            var crash = events.Single(e => e.Name() == "app_unclean_exit");
            var end = events.Single(e => e.Name() == "session_end");
            Assert.Equal(crashedSession, crash.Str("session_id"));
            Assert.Equal(crashedSession, crash.Props().Str("previous_session_id"));
            Assert.Equal("571", crash.Props().Str("previous_build"));
            Assert.Equal("1.4.0", crash.Props().Str("previous_app_version"));
            Assert.Equal(crash.Str("original_timestamp"), end.Str("original_timestamp"));
            Assert.Equal("2027-01-15T08:00:25.000Z", end.Str("original_timestamp"));
            Assert.Equal(25, end.Props().GetProperty("duration_s").GetInt64());
            Assert.Equal(new[] { "app_unclean_exit", "session_end", "session_start", "game_open" }, device.Transport.Names);
            Assert.NotEqual(crashedSession, next.SessionId);
        }

        [Fact]
        public void AReloadedWebGLPageIsNotACrash()
        {
            // Seen in a WebGL build: a reload or a closed tab has no quit event.
            var device = new Device();
            var page = device.Launch(platform: "web");
            page.Update();
            var session = page.SessionId;
            device.Clock.Advance(25);
            page.Update();
            device.Clock.Advance(600);
            var reloaded = device.Launch(platform: "web");
            reloaded.Update();
            Assert.DoesNotContain("app_unclean_exit", device.Transport.Names);
            var end = device.Transport.AllEvents.Single(e => e.Name() == "session_end");
            Assert.Equal(session, end.Str("session_id"));
            Assert.Equal(25, end.Props().GetProperty("duration_s").GetInt64());
        }

        [Fact]
        public void AnAppKilledInTheBackgroundIsNotACrash()
        {
            var device = new Device();
            var client = device.Launch();
            client.Update();
            client.OnFocusChanged(false);
            device.Clock.Advance(3600);
            var next = device.Launch();
            next.Update();
            Assert.DoesNotContain("app_unclean_exit", device.Transport.Names);
            Assert.Contains("session_end", device.Transport.Names);
        }

        [Fact]
        public void ANormalQuitIsNotACrash()
        {
            var device = new Device();
            var client = device.Launch();
            client.Update();
            client.OnQuit();
            Assert.Equal(1, client.QueueSize); // session_end waits on disk for the next launch.
            var next = device.Launch();
            next.Update();
            Assert.DoesNotContain("app_unclean_exit", device.Transport.Names);
            Assert.Equal(new[] { "session_end", "session_start", "game_open" }, device.Transport.Names);
        }
    }

    /// <summary>A dedicated server build is not a player (G-2533).</summary>
    public class ServerBuildTests
    {
        static readonly string[] Automatic = { "game_open", "session_start", "session_end", "app_unclean_exit" };

        /// <summary>Two launches of a server: the first ends without a quit (a crash), the second quits.</summary>
        static List<string> RunServer(Device device, bool trackServerBuilds)
        {
            device.ServerBuild = true;
            var names = new List<string>();
            var options = Device.Options(o => o.TrackServerBuilds = trackServerBuilds);
            var first = device.Launch(options);
            first.Track("match_end", new Dictionary<string, object> { ["winner"] = "red" });
            first.SendNow();
            names.AddRange(device.Transport.Names);
            device.Clock.Advance(60);
            var second = device.Launch(options);
            second.Identify("player-7");
            second.Track("match_end", new Dictionary<string, object> { ["winner"] = "blue" });
            second.BeginShutdown();
            second.SendNow();
            second.OnQuit();
            names.AddRange(device.Transport.Names);
            return names;
        }

        [Fact]
        public void AServerBuildSendsNoClientEventsAndNoInstall()
        {
            var device = new Device();
            var names = RunServer(device, trackServerBuilds: false);
            foreach (var name in Automatic) Assert.DoesNotContain(name, names);
            Assert.Equal(new[] { "match_end", "identify", "match_end" }, names);
            Assert.True(device.Log.Has("server build"), string.Join("\n", device.Log.Lines));
            // Track() still has an anonymous ID and a session ID.
            var events = device.Transport.AllEvents;
            Assert.All(events, e => Assert.False(string.IsNullOrEmpty(e.Str("anonymous_id"))));
            Assert.All(events, e => Assert.False(string.IsNullOrEmpty(e.Str("session_id"))));
        }

        [Fact]
        public void AServerBuildWithTrackServerBuildsSendsTheClientEvents()
        {
            var device = new Device();
            var names = RunServer(device, trackServerBuilds: true);
            foreach (var name in Automatic) Assert.Contains(name, names);
            Assert.Equal(2, names.Count(n => n == "game_open"));
            Assert.False(device.Log.Has("server build"));
        }

        [Fact]
        public void AClientBuildIgnoresTrackServerBuilds()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.TrackServerBuilds = false));
            client.SendNow();
            Assert.Equal(new[] { "session_start", "game_open" }, device.Transport.Names);
            Assert.False(device.Log.Has("server build"));
        }
    }

    public class ConsentTests
    {
        [Fact]
        public void NothingIsSentOrStoredBeforeConsent()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.ConsentRequired = true));
            client.Track("before_consent");
            client.TrackError("before consent");
            client.Identify("player_7", new Dictionary<string, object> { ["plan"] = "free" });
            client.Update();
            device.Clock.Advance(20);
            client.Update();
            client.OnQuit();
            Assert.Empty(device.Transport.Requests);
            Assert.Empty(device.Storage.Blobs);
            Assert.Equal("", client.AnonymousId);
            Assert.Equal("https://x.example/", client.LinkUrl("https://x.example/"));

            client.SetConsent(true);
            client.Track("after_consent");
            client.Update();
            Assert.Equal(new[] { "session_start", "game_open", "identify", "after_consent" }, device.Transport.Names);
            Assert.Equal("player_7", device.Transport.AllEvents[2].Str("user_id"));
            Assert.Equal("free", device.Transport.AllEvents[2].GetProperty("traits").Str("plan"));
        }

        [Fact]
        public void TheChoiceIsSavedAndWithdrawingDeletesTheIdAndTheQueue()
        {
            var device = new Device();
            var options = Device.Options(o => o.ConsentRequired = true);
            var client = device.Launch(options);
            client.SetConsent(true);
            client.Update();
            client.OnQuit();

            var second = device.Launch(Device.Options(o => o.ConsentRequired = true));
            Assert.True(second.IsActive); // Consent from the first launch.
            device.Transport.Statuses.Enqueue(0);
            second.Update();
            Assert.True(second.QueueSize > 0);
            second.SetConsent(false);
            Assert.False(second.IsActive);
            Assert.Equal(0, second.QueueSize);
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.QueueName));
            Assert.DoesNotContain("anonymous_id", device.Storage.Blobs[AnalyticsClient.StateName]);
            Assert.Contains("consent.granted=false", device.Storage.Blobs[AnalyticsClient.StateName]);
            second.Track("after_withdraw");

            var third = device.Launch(Device.Options(o => o.ConsentRequired = false));
            Assert.False(third.IsActive); // A saved "no" wins over ConsentRequired = false.
            third.Update();
            Assert.Empty(device.Transport.Requests);
        }

        [Fact]
        public void ConsentAgainMakesANewAnonymousId()
        {
            var device = new Device();
            var client = device.Launch();
            var first = client.AnonymousId;
            client.SetConsent(false);
            client.SetConsent(true);
            Assert.NotEqual(first, client.AnonymousId);
            client.Update();
            var open = device.Transport.AllEvents.Last(e => e.Name() == "game_open");
            Assert.Equal(JsonValueKind.True, open.Props().GetProperty("is_first_open").ValueKind);
        }
    }

    public class ErrorTests
    {
        static JsonElement Single(Device device, string source = null) =>
            device.Transport.AllEvents.Single(e => e.Name() == "error" && (source == null || e.Props().Str("source") == source));

        [Fact]
        public void TrackErrorSendsTheErrorPropertiesAndBreadcrumbs()
        {
            var device = new Device();
            var client = device.Launch();
            client.Track("level_start", new Dictionary<string, object> { ["level"] = 3 });
            client.TrackError("Save failed for slot 3", new TrackErrorOptions { Severity = "critical", ErrorType = "SaveFailed", Handled = false, Properties = new Dictionary<string, object> { ["level"] = 3 } });
            client.Update();
            var props = Single(device).Props();
            Assert.Equal("critical", props.Str("severity"));
            Assert.Equal("Save failed for slot 3", props.Str("message"));
            Assert.Equal(JsonValueKind.False, props.GetProperty("handled").ValueKind);
            Assert.Equal("manual", props.Str("source"));
            Assert.Equal("SaveFailed", props.Str("error_type"));
            Assert.Equal(3, props.GetProperty("level").GetInt32());
            Assert.Equal("session_start,game_open,level_start", props.Str("breadcrumbs"));
            Assert.Matches("^[0-9a-f]{16}$", props.Str("fingerprint"));
            Assert.False(props.TryGetProperty("file", out _));
        }

        [Fact]
        public void TheFingerprintIgnoresNumbersAndQuotedValues()
        {
            Assert.Equal(ErrorTools.Fingerprint("", "Slot 3 failed for 'ana'", "", ""), ErrorTools.Fingerprint("", "Slot 12 failed for 'bob'", "", ""));
            Assert.NotEqual(ErrorTools.Fingerprint("A", "x", "", ""), ErrorTools.Fingerprint("B", "x", "", ""));
            // With a place in the code, the line number and the message do not count.
            Assert.Equal(ErrorTools.Fingerprint("E", "one", "Assets/A.cs", "A.Run"), ErrorTools.Fingerprint("E", "two", "Assets/A.cs", "A.Run"));
            // Same as the Godot SDK: sha1("SaveFailed|res://main.gd|_save").left(16).
            Assert.Equal("019c506f3cbbadbb", ErrorTools.Fingerprint("SaveFailed", "", "res://main.gd", "_save"));
            Assert.Equal("97b0f1841c0d66a1", ErrorTools.Fingerprint("", "Slot 3 failed for 'ana'", "", ""));
        }

        [Fact]
        public void AnUnhandledExceptionLogIsParsedFromTheUnityStack()
        {
            var device = new Device();
            var client = device.Launch();
            client.CaptureLog("NullReferenceException: Object reference not set to an instance of an object",
                "Game.Player.Jump () (at Assets/Scripts/Player.cs:42)\nGame.Player.Update () (at Assets/Scripts/Player.cs:12)\n", LogKind.Exception);
            client.Update();
            var props = Single(device, "unity_log").Props();
            Assert.Equal("NullReferenceException", props.Str("error_type"));
            Assert.Equal("Assets/Scripts/Player.cs", props.Str("file"));
            Assert.Equal(42, props.GetProperty("line").GetInt32());
            Assert.Equal("Game.Player.Jump", props.Str("function"));
            Assert.Equal("exception", props.Str("log_type"));
            Assert.Equal("error", props.Str("severity"));
            Assert.Equal(JsonValueKind.False, props.GetProperty("handled").ValueKind);
            Assert.StartsWith("Game.Player.Jump () (at", props.Str("stack"));
        }

        [Fact]
        public void AnEditorErrorInPlayModeIsNotCaptured()
        {
            // Seen in Unity 6 Play mode: an exception of the editor's search indexing.
            var device = new Device();
            var client = device.Launch();
            client.CaptureLog("ArgumentOutOfRangeException: Index was out of range. Must be non-negative and less than the size of the collection.",
                "  at System.Collections.Generic.List`1[T].get_Item (System.Int32 index) [0x00009] in <2d46e6b0>:0 \n" +
                "  at UnityEditor.Search.SearchDatabase+<EnumerateAll>d__78.MoveNext () [0x00089] in /Users/bokken/build/output/unity/unity/Modules/QuickSearch/Editor/Indexing/SearchDatabase.cs:351 \n", LogKind.Exception);
            client.CaptureLog("Inspector failed", "UnityEngine.Debug:LogError (object)\nUnityEditor.InspectorWindow:OnGUI () (at /Users/bokken/InspectorWindow.cs:10)\n", LogKind.Error);
            client.CaptureLog("Boss failed", "UnityEngine.Debug:LogError (object)\nBoss:Update () (at Assets/Boss.cs:5)\n", LogKind.Error);
            client.Update();
            var props = Single(device, "unity_log").Props();
            Assert.Equal("Boss failed", props.Str("message"));
        }

        [Fact]
        public void ADebugLogErrorSkipsTheLoggingFrames()
        {
            var frame = ErrorTools.FirstFrame("UnityEngine.Debug:LogError (object)\nInventory:Load (string) (at Assets/Inventory.cs:88)\n");
            Assert.Equal("Inventory.Load", frame.Function);
            Assert.Equal("Assets/Inventory.cs", frame.File);
            Assert.Equal(88, frame.Line);
            var release = ErrorTools.FirstFrame("UnityEngine.Debug:LogError(Object)\nInventory:Load(String)\n");
            Assert.Equal("Inventory.Load", release.Function);
            Assert.Equal("", release.File);
            var dotnet = ErrorTools.FirstFrame("   at Game.Save.Write(String path) in C:\\src\\Save.cs:line 17\n   at Game.Save.Run()");
            Assert.Equal("Game.Save.Write", dotnet.Function);
            Assert.Equal("C:/src/Save.cs", dotnet.File);
            Assert.Equal(17, dotnet.Line);
            // A macOS Mono release player (Unity 6): the module ID changes with each build, so it is not a file.
            var player = ErrorTools.FirstFrame("E2ERunner.Update () (at <b67cbc119ab74ac9ad98b7eaeaad2a9e>:0)\nUnityEngine.StackTraceUtility:ExtractStringFromExceptionInternal(Object, String&, String&)\n");
            Assert.Equal("E2ERunner.Update", player.Function);
            Assert.Equal("", player.File);
            Assert.Equal(0, player.Line);
            var mono = ErrorTools.FirstFrame("  at Game.Save.Write (System.String path) [0x00012] in /src/Save.cs:17 ");
            Assert.Equal("Game.Save.Write", mono.Function);
            Assert.Equal("/src/Save.cs", mono.File);
            Assert.Equal("System.IO.IOException".Split('.').Last(), ErrorTools.ExceptionType("System.IO.IOException: Sharing violation"));
            Assert.Equal("", ErrorTools.ExceptionType("Something went wrong"));
        }

        [Fact]
        public void TrackErrorWithAnExceptionUsesItsTypeAndStack()
        {
            var device = new Device();
            var client = device.Launch();
            try
            {
                throw new InvalidOperationException("Bad state 7");
            }
            catch (Exception e)
            {
                client.TrackError(e, new TrackErrorOptions { Severity = "warning" });
            }
            client.Update();
            var props = Single(device).Props();
            Assert.Equal("InvalidOperationException", props.Str("error_type"));
            Assert.Equal("Bad state 7", props.Str("message"));
            Assert.Equal("warning", props.Str("severity"));
            Assert.Contains("TrackErrorWithAnExceptionUsesItsTypeAndStack", props.Str("function"));
        }

        [Fact]
        public void WarningsAndTheSdkOwnLogsAreNotCapturedByDefault()
        {
            var device = new Device();
            var client = device.Launch();
            client.CaptureLog("Low memory", "", LogKind.Warning);
            client.CaptureLog("Manalens: could not write", "", LogKind.Error);
            client.Update();
            Assert.DoesNotContain("error", device.Transport.Names);

            var other = new Device();
            var withWarnings = other.Launch(Device.Options(o => o.CaptureWarnings = true));
            withWarnings.CaptureLog("Low memory", "", LogKind.Warning);
            withWarnings.Update();
            var props = Single(other).Props();
            Assert.Equal("warning", props.Str("severity"));
            Assert.Equal("log_warning", props.Str("error_type"));

            var off = new Device();
            var noCapture = off.Launch(Device.Options(o => o.CaptureErrors = false));
            noCapture.CaptureLog("NullReferenceException: x", "", LogKind.Exception);
            noCapture.Update();
            Assert.DoesNotContain("error", off.Transport.Names);
        }

        [Fact]
        public void TheSameErrorIsSentOnceASecond()
        {
            var device = new Device();
            var client = device.Launch();
            for (var i = 0; i < 3; i++) client.TrackError("Boom " + i);
            device.Clock.Advance(1.1);
            client.TrackError("Boom 99");
            client.BeginShutdown();
            Assert.Equal(2, device.Transport.Names.Count(n => n == "error"));
            Assert.Equal(2, device.Transport.AllEvents.Single(e => e.Name() == "session_end").Props().GetProperty("errors_dropped").GetInt32());
        }

        [Fact]
        public void AtMost20ErrorsIn10SecondsAndFivePerUpdateAndTheSessionCap()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => { o.MaxAutoErrorsPerSession = 7; o.FlushAt = 50; }));
            for (var i = 0; i < 25; i++) client.TrackError("distinct", new TrackErrorOptions { Fingerprint = "fp" + i });
            Assert.Equal(20, client.QueueSize - 2);
            device.Clock.Advance(11);
            for (var i = 0; i < 8; i++) client.CaptureLog("Error kind " + i, "Type" + i + ".Run () (at Assets/T.cs:1)", LogKind.Error);
            client.Update(); // Five per update; three are counted as dropped.
            device.Clock.Advance(0.1);
            for (var i = 0; i < 4; i++) client.CaptureLog("Other " + i, "Other" + i + ".Run () (at Assets/O.cs:1)", LogKind.Error);
            client.Update(); // Two more reach the session cap of 7.
            client.BeginShutdown();
            var errors = device.Transport.AllEvents.Where(e => e.Name() == "error").ToList();
            Assert.Equal(20 + 7, errors.Count);
            Assert.Equal(5 + 3 + 2, device.Transport.AllEvents.Single(e => e.Name() == "session_end").Props().GetProperty("errors_dropped").GetInt32());
        }

        [Fact]
        public void BeforeSendCanChangeOrDropAnError()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.BeforeSend = e =>
            {
                if ((string)e["event"] != "error") return e;
                var props = (Dictionary<string, object>)e["properties"];
                if ((string)props["message"] == "drop me") return null;
                props["message"] = "redacted";
                props["extra"] = new object();
                return e;
            }));
            client.TrackError("drop me");
            client.TrackError("player ana@example.com failed");
            client.Update();
            var props = Single(device).Props();
            Assert.Equal("redacted", props.Str("message"));
            Assert.False(props.TryGetProperty("extra", out _));

            client.SetBeforeSend(_ => throw new Exception("bug"));
            device.Clock.Advance(2);
            client.TrackError("another");
            Assert.True(device.Log.Has("BeforeSend threw"));
        }

        [Fact]
        public void CaptureLogIsSafeFromOtherThreads()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.FlushAt = 50));
            var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
            {
                for (var i = 0; i < 50; i++) client.CaptureLog("T" + t + " " + i, "", LogKind.Error);
                client.Track("from_thread_" + t);
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            for (var i = 0; i < 20 && client.QueueSize > 0; i++) client.Update();
            Assert.Equal(0, client.QueueSize);
            Assert.Equal(8, device.Transport.Names.Count(n => n.StartsWith("from_thread_")));
            // 400 errors arrived with one fingerprint (the message without numbers): one is sent.
            Assert.Equal(1, device.Transport.Names.Count(n => n == "error"));
        }
    }

    public class HelperTests
    {
        [Theory]
        [InlineData("WindowsPlayer", "windows")]
        [InlineData("WindowsEditor", "windows")]
        [InlineData("OSXPlayer", "macos")]
        [InlineData("LinuxPlayer", "linux")]
        [InlineData("Android", "android")]
        [InlineData("IPhonePlayer", "ios")]
        [InlineData("WebGLPlayer", "web")]
        [InlineData("tvOS", "other")]
        [InlineData("PS5", "ps5")]
        [InlineData("GameCoreXboxSeries", "xbox_series")]
        [InlineData("GameCoreXboxOne", "xbox_one")]
        [InlineData("XboxOne", "xbox_one")]
        [InlineData("Switch", "switch")]
        [InlineData("Switch2", "switch2")]
        [InlineData("PS4", "other")]
        public void PlatformMapping(string unity, string expected) => Assert.Equal(expected, Platforms.FromRuntimePlatform(unity));

        [Fact]
        public void UuidsAreVersion4()
        {
            var ids = Enumerable.Range(0, 200).Select(_ => Ids.NewUuid()).ToList();
            Assert.Equal(200, ids.Distinct().Count());
            Assert.All(ids, id => Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", id));
        }

        [Fact]
        public void StateFileRoundTripsEscapedValues()
        {
            var state = new StateFile();
            state.Set("a", "line\nbreak\\slash=equals\r");
            state.SetDouble("b", 1.25);
            state.SetBool("c", true);
            Assert.True(StateFile.TryParse(state.Serialize(), out var parsed));
            Assert.Equal("line\nbreak\\slash=equals\r", parsed.Get("a"));
            Assert.Equal(1.25, parsed.GetDouble("b", 0));
            Assert.True(parsed.GetBool("c", false));
        }

        [Fact]
        public void FileStorageWritesReadsAndDeletes()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ua-unity-" + Guid.NewGuid());
            var storage = new FileStorage(folder);
            Assert.Equal(ReadStatus.Missing, storage.Read("x", out var none));
            Assert.Null(none);
            Assert.True(storage.Write("x", "one"));
            Assert.True(storage.Write("x", "two")); // Replaces the file with a rename.
            Assert.Equal(ReadStatus.Ok, storage.Read("x", out var text));
            Assert.Equal("two", text);
            Assert.False(System.IO.File.Exists(System.IO.Path.Combine(folder, "x.txt.tmp")));
            Assert.True(storage.Delete("x"));
            Assert.Equal(ReadStatus.Missing, storage.Read("x", out _));
            // A file that exists but cannot be read (here: a folder with its name) is Failed, not Missing.
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(folder, "y.txt"));
            Assert.Equal(ReadStatus.Failed, storage.Read("y", out _));
            Assert.False(storage.Write("y", "z"));
            System.IO.Directory.Delete(folder, true);
        }

        [Fact]
        public void ACorruptQueueFileIsIgnored()
        {
            var device = new Device();
            device.Storage.Blobs[AnalyticsClient.QueueName] = "garbage\nnot\ta\tjson\n1800000000\tid\t{\"type\":\"track\"\n";
            var client = device.Launch();
            Assert.Equal(2, client.QueueSize);
        }
    }
}
