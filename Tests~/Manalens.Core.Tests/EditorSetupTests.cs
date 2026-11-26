using System.Linq;
using System.Text.Json;
using ManalensSdk;
using Xunit;

namespace Manalens.Tests
{
    /// <summary>Project Settings > Manalens (G-2532): the editor test event and the Play mode state.</summary>
    public class EditorSetupTests
    {
        [Fact]
        public void TheTestEventIsOneEditorEventWithoutInstallOrSession()
        {
            var body = TestEvent.Body("macos", "00000000-0000-4000-8000-000000000001", "1.4.0", "steam", 1800000000);
            var events = JsonDocument.Parse(body).RootElement.GetProperty("events").EnumerateArray().ToList();
            var e = Assert.Single(events);
            Assert.Equal("track", e.Str("type"));
            Assert.Equal(TestEvent.Name, e.Str("event"));
            Assert.Equal("app", e.Str("channel"));
            Assert.Equal(AnalyticsClient.LibraryVersion, e.Str("version"));
            Assert.Equal("2027-01-15T08:00:00.000Z", e.Str("original_timestamp"));
            Assert.Equal("00000000-0000-4000-8000-000000000001", e.Str("anonymous_id"));
            Assert.Matches("^[0-9a-f-]{36}$", e.Str("session_id"));
            var app = e.GetProperty("context").GetProperty("app");
            Assert.Equal(new[] { "macos", "unity", "editor", "1.4.0", "steam" }, new[] { "platform", "engine", "build", "app_version", "storefront" }.Select(k => app.Str(k)));
            Assert.Equal("unity_editor", e.Props().Str("source"));
            Assert.Equal("https://collect.example.com/v0/events", TestEvent.Url(" https://collect.example.com/ "));
        }

        [Fact]
        public void TheTestResultSaysWhatTheCollectorDid()
        {
            Assert.StartsWith("Received (HTTP 202)", TestEvent.Describe(202, "{\"accepted\":1,\"rejected\":0,\"errors\":[]}", null));
            Assert.Equal("Refused (HTTP 401): the collector does not accept this key here. UNAUTHORIZED: Malformed publishable key",
                TestEvent.Describe(401, "{\"error\":{\"code\":\"UNAUTHORIZED\",\"message\":\"Malformed publishable key\"}}", null));
            Assert.StartsWith("Refused (HTTP 202). INVALID_EVENT", TestEvent.Describe(202, "{\"accepted\":0,\"rejected\":1,\"errors\":[{\"index\":0,\"code\":\"INVALID_EVENT\",\"message\":\"x\"}]}", null));
            Assert.StartsWith("Not sent: no answer from the endpoint (Cannot connect)", TestEvent.Describe(0, "", "Cannot connect"));
        }

        [Fact]
        public void LastSendResultAndRecentEventsShowWhatHappened()
        {
            var device = new Device();
            var client = device.Launch();
            Assert.Equal("", client.LastSendResult);
            Assert.Equal(new[] { "session_start", "game_open" }, client.RecentEvents.Select(json => JsonDocument.Parse(json).RootElement.Name()));
            client.Update();
            Assert.Equal("HTTP 202: 2 events stored", client.LastSendResult);
            Assert.Equal(2, client.RecentEvents.Length); // Kept after the send.
            client.Track("level_start");
            device.Transport.Statuses.Enqueue(400);
            device.Transport.ResponseBody = "{\"error\":{\"code\":\"INVALID_EVENT\",\"message\":\"bad\"}}";
            client.SendNow();
            Assert.Equal("HTTP 400 INVALID_EVENT: bad: 1 events dropped", client.LastSendResult);
            device.Transport.ResponseBody = null;
            client.Track("offline");
            device.Transport.Statuses.Enqueue(0);
            client.SendNow();
            Assert.Equal("network error: kept in the queue, sent again later", client.LastSendResult);
            for (var i = 0; i < 30; i++) client.Track("e" + i);
            Assert.Equal(AnalyticsClient.RecentCount, client.RecentEvents.Length);
            Assert.Equal("e29", JsonDocument.Parse(client.RecentEvents.Last()).RootElement.Name());
            client.SetConsent(false);
            Assert.Empty(client.RecentEvents);
        }
    }
}
