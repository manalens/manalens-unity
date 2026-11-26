using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ManalensSdk;
using Xunit;

namespace Manalens.Tests
{
    /// <summary>G-2527: BeforeSend gets every event, can sample and scrub, and cannot break the envelope.</summary>
    public class BeforeSendTests
    {
        static Dictionary<string, object> Props(Dictionary<string, object> e) => (Dictionary<string, object>)e["properties"];

        static void Send(AnalyticsClient client)
        {
            client.RequestFlush();
            client.Update();
        }

        [Fact]
        public void RunsOnEveryEventAndCanSampleAndScrub()
        {
            var device = new Device();
            var seen = new List<string>();
            var jumps = 0;
            var client = device.Launch(Device.Options(o =>
            {
                o.FlushAt = 50;
                o.BeforeSend = e =>
                {
                    seen.Add(e["type"] + ":" + (e.TryGetValue("event", out var n) ? n : ""));
                    if (e.TryGetValue("event", out var name) && (string)name == "player_jump" && jumps++ % 10 != 0) return null; // keep 1 in 10
                    Props(e).Remove("email");
                    if (e.TryGetValue("traits", out var traits)) ((Dictionary<string, object>)traits).Remove("email");
                    return e;
                };
            }));
            for (var i = 0; i < 20; i++) client.Track("player_jump", new Dictionary<string, object> { ["index"] = i });
            client.Track("signup", new Dictionary<string, object> { ["email"] = "ana@example.com", ["level"] = 1 });
            client.Identify("player_42", new Dictionary<string, object> { ["email"] = "ana@example.com", ["plan"] = "supporter" });
            client.TrackError("Save failed");
            Send(client);

            Assert.Contains("track:session_start", seen);
            Assert.Contains("track:game_open", seen);
            Assert.Contains("identify:", seen);
            Assert.Contains("track:error", seen);
            var events = device.Transport.AllEvents;
            Assert.Equal(2, events.Count(e => e.Name() == "player_jump"));
            var signup = events.Single(e => e.Name() == "signup");
            Assert.False(signup.Props().TryGetProperty("email", out _));
            Assert.Equal(1, signup.Props().GetProperty("level").GetInt32());
            var identify = events.Single(e => e.Str("type") == "identify");
            Assert.False(identify.GetProperty("traits").TryGetProperty("email", out _));
            Assert.Equal("supporter", identify.GetProperty("traits").Str("plan"));
            Assert.Equal("player_42", identify.Str("user_id"));
            // Dropped events are not breadcrumbs.
            var crumbs = events.Single(e => e.Name() == "error").Props().Str("breadcrumbs");
            Assert.Equal(2, crumbs.Split(',').Count(name => name == "player_jump"));
        }

        [Fact]
        public void ADroppedErrorStillCountsInTheErrorLimits()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.BeforeSend = e =>
                (string)e["event"] == "error" && ((string)Props(e)["message"]).Contains("secret") ? null : e));
            client.TrackError("a secret value");
            client.TrackError("a secret value"); // the 1-second limit drops it: errors_dropped 1
            client.BeginShutdown();
            Assert.DoesNotContain("error", device.Transport.Names);
            Assert.Equal(1, device.Transport.AllEvents.Single(e => e.Name() == "session_end").Props().GetProperty("errors_dropped").GetInt32());
        }

        [Theory]
        [InlineData("message_id")]
        [InlineData("anonymous_id")]
        [InlineData("session_id")]
        [InlineData("type")]
        [InlineData("app")]
        [InlineData("event")]
        [InlineData("properties")]
        public void AHookThatBreaksTheEnvelopeSendsTheOriginalEvent(string broken)
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.FlushAt = 50));
            client.SetBeforeSend(e =>
            {
                if ((string)e["event"] != "level_start") return e;
                Props(e)["level"] = 99;
                switch (broken)
                {
                    case "app": ((Dictionary<string, object>)((Dictionary<string, object>)e["context"])["app"]).Remove("build"); break;
                    case "event": e["event"] = 42; break;
                    case "properties": e["properties"] = "nope"; break;
                    case "type": e["type"] = "identify"; break;
                    case "message_id": e["message_id"] = "changed"; break;
                    default: e.Remove(broken); break;
                }
                return e;
            });
            client.Track("level_start", new Dictionary<string, object> { ["level"] = 3 });
            Send(client);
            var sent = device.Transport.AllEvents.Single(e => e.Name() == "level_start");
            Assert.Equal(3, sent.Props().GetProperty("level").GetInt32());
            Assert.Equal("track", sent.Str("type"));
            Assert.NotEqual("changed", sent.Str("message_id"));
            Assert.NotNull(sent.Str("anonymous_id"));
            Assert.NotNull(sent.Str("session_id"));
            Assert.Equal("571", sent.GetProperty("context").GetProperty("app").Str("build"));
            Assert.True(device.Log.Has("the original event is sent"));
        }

        [Fact]
        public void AThrowingHookDropsTheEventAndANestedEventIsDropped()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.FlushAt = 50));
            client.SetBeforeSend(e => (string)e["event"] == "boom" ? throw new Exception("bug") : e);
            client.Track("boom");
            Assert.True(device.Log.Has("BeforeSend threw"));
            client.SetBeforeSend(e =>
            {
                if ((string)e["event"] == "level_start") client.Track("from_the_hook");
                return e;
            });
            client.Track("level_start");
            client.SetBeforeSend(null);
            client.Track("after_removal", new Dictionary<string, object> { ["email"] = "kept@example.com" });
            Send(client);
            var names = device.Transport.Names;
            Assert.DoesNotContain("boom", names);
            Assert.DoesNotContain("from_the_hook", names);
            Assert.Contains("level_start", names);
            Assert.True(device.Log.Has("inside BeforeSend is dropped"));
            Assert.Equal("kept@example.com", device.Transport.AllEvents.Single(e => e.Name() == "after_removal").Props().Str("email"));
        }

        [Fact]
        public void TheHookCanChangeTheNameAndRemoveTheUserId()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.FlushAt = 50));
            client.Identify("player_42");
            client.SetBeforeSend(e =>
            {
                if ((string)e["type"] != "track" || (string)e["event"] != "old_name") return e;
                e["event"] = "new_name";
                e.Remove("user_id");
                return e;
            });
            client.Track("old_name");
            Send(client);
            var sent = device.Transport.AllEvents.Single(e => e.Name() == "new_name");
            Assert.Null(sent.Str("user_id"));
        }
    }
}
