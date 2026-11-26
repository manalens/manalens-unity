using System.Linq;
using System.Text.Json;
using ManalensSdk;
using Xunit;

namespace Manalens.Tests
{
    /// <summary>
    /// P-2783: no breadcrumbs or error state survive a consent withdrawal or a
    /// reset, and captured log messages are cleaned before they are queued
    /// (the rules of the Unreal plugin, Manalens.Errors.LogMessageCleaning).
    /// </summary>
    public class ErrorPrivacyTests
    {
        static readonly string[] Home = { "/Users/anna/", "/Users/anna/Documents/" };

        static void Send(AnalyticsClient client)
        {
            client.RequestFlush();
            client.Update();
        }

        static JsonElement LastError(Device device) => device.Transport.AllEvents.Last(e => e.Name() == "error");

        [Fact]
        public void ConsentWithdrawalClearsTheErrorHistory()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.FlushAt = 50));
            client.Track("secret_level");
            client.TrackError("Save failed");
            client.TrackError("Save failed"); // the 1-second limit drops it: errors_dropped 1
            client.SetConsent(false);
            client.SetConsent(true);
            client.TrackError("Save failed");
            client.Update();
            var error = LastError(device);
            Assert.Equal("session_start,game_open", error.Props().Str("breadcrumbs"));
            client.BeginShutdown();
            var end = device.Transport.AllEvents.Last(e => e.Name() == "session_end");
            Assert.False(end.Props().TryGetProperty("errors_dropped", out _));
            Assert.DoesNotContain("secret_level", device.Transport.Names);
        }

        [Fact]
        public void ResetSendsTheOldDroppedCountThenClearsTheErrorHistory()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.FlushAt = 50));
            client.Identify("player_42");
            client.Track("secret_level");
            client.TrackError("Save failed");
            client.TrackError("Save failed"); // dropped: errors_dropped 1
            client.Reset();
            client.TrackError("Save failed");
            client.Update();
            var end = device.Transport.AllEvents.Single(e => e.Name() == "session_end");
            Assert.Equal(1, end.Props().GetProperty("errors_dropped").GetInt32());
            var error = LastError(device);
            Assert.Equal("session_start", error.Props().Str("breadcrumbs"));
            Assert.Null(error.Str("user_id"));
        }

        [Theory]
        [InlineData("Failed to open /Users/anna/Saved/x.sav", "Failed to open ~/Saved/x.sav")]
        [InlineData("at \\Users\\anna\\Saved", "at ~\\Saved")]
        [InlineData("read /Users/dave/Library/a.ini", "read ~/Library/a.ini")]
        [InlineData("read /home/bob/game/log.txt", "read ~/game/log.txt")]
        [InlineData("read C:\\Users\\Carol\\AppData\\x.ini", "read ~\\AppData\\x.ini")]
        [InlineData("read d:/users/Carol/x.ini", "read ~/x.ini")]
        [InlineData("GET https://user:pass@api.example.com/v1/items?id=3&sig=abc#frag failed", "GET https://api.example.com/v1/items failed")]
        [InlineData("connect wss://u@play.example.com?ticket=1", "connect wss://play.example.com")]
        [InlineData("GET https://api.example.com/v1/items", "GET https://api.example.com/v1/items")]
        [InlineData("No account for jane.doe+x@mail.example.com.", "No account for [email].")]
        [InlineData("Actor@Level failed", "Actor@Level failed")]
        [InlineData("api_key=abc123 token=xyz&secret=s3 PASSWORD=hunter2", "api_key=[redacted] token=[redacted]&secret=[redacted] PASSWORD=[redacted]")]
        [InlineData("Authorization: Bearer abc.def", "Authorization: [redacted]")]
        [InlineData("sent bearer xyz.123 to the server", "sent bearer [redacted] to the server")]
        [InlineData("session ABCDEFGHIJKLMNOPQRSTUV_- expired", "session [redacted] expired")]
        [InlineData("session ABCDEFGHIJKLMNOPQRSTUVW expired", "session ABCDEFGHIJKLMNOPQRSTUVW expired")]
        [InlineData("id 6f1c2a3b-4d5e-4f60-8a7b-9c0d1e2f3a4b", "id [redacted]")]
        [InlineData("Object reference not set to an instance of an object", "Object reference not set to an instance of an object")]
        [InlineData("password=\"hunter2 two\" and key='a b'", "password=\"[redacted]\" and key='[redacted]'")]
        [InlineData("{\"token\": \"abc def\", \"api_key\":\"k1\", \"user\": 3}", "{\"token\": \"[redacted]\", \"api_key\":\"[redacted]\", \"user\": 3}")]
        [InlineData("{\"password\": \"p w\", \"secret\": 42}", "{\"password\": \"[redacted]\", \"secret\": [redacted]}")]
        [InlineData("token: abc123 password : hunter2", "token: [redacted] password : [redacted]")]
        [InlineData("\"Authorization\": \"Bearer abc\"", "\"Authorization\": \"[redacted]\"")]
        [InlineData("header token: Bearer abc", "header token: [redacted] [redacted]")]
        [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln. failed", "jwt [redacted]. failed")]
        [InlineData("blob aGVsbG8gd29ybGQgaGVsbG8+d29ybGQ end", "blob [redacted] end")]
        [InlineData("blob aGVsbG8gd29ybGQgaGVsbG8vd29ybGQ= end", "blob [redacted] end")]
        [InlineData("load Assets/Levels/boss_room_final/arena.unity failed", "load Assets/Levels/boss_room_final/arena.unity failed")]
        public void CleanLogMessageRemovesPersonalData(string message, string expected)
        {
            Assert.Equal(expected, ErrorTools.CleanLogMessage(message, Home));
        }

        [Fact]
        public void TheRealHomeFolderBecomesATilde()
        {
            var home = ErrorTools.HomeDirs();
            if (home.Count == 0) return;
            var cleaned = ErrorTools.CleanLogMessage(home[0].TrimEnd('/', '\\') + "/Saved/a.sav", home);
            Assert.Equal("~/Saved/a.sav", cleaned);
        }

        [Fact]
        public void ACapturedLogReachesTheQueueCleanedAndTrackErrorTextDoesNot()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.FlushAt = 50));
            client.CaptureLog("Cannot load /Users/anna/Saved/a.sav for anna@example.com", "", LogKind.Error);
            Send(client);
            var first = LastError(device);
            Assert.Equal("Cannot load ~/Saved/a.sav for [email]", first.Props().Str("message"));

            device.Clock.Advance(2);
            client.CaptureLog("Cannot load /home/bob/Saved/a.sav for bob@example.org", "", LogKind.Error);
            Send(client);
            var second = LastError(device);
            Assert.NotEqual(first.Str("message_id"), second.Str("message_id"));
            Assert.Equal(first.Props().Str("fingerprint"), second.Props().Str("fingerprint"));

            // The exception type is a class name, not a token: kept even at 24 or more characters.
            device.Clock.Advance(2);
            client.CaptureLog("InvalidOperationException: bad state for token=abc123", "Game.Save () (at Assets/Save.cs:3)", LogKind.Exception);
            Send(client);
            var exception = LastError(device);
            Assert.Equal("InvalidOperationException: bad state for token=[redacted]", exception.Props().Str("message"));
            Assert.Equal("InvalidOperationException", exception.Props().Str("error_type"));

            device.Clock.Advance(2);
            client.TrackError("Mail support@example.com about /Users/anna/x");
            Send(client);
            Assert.Equal("Mail support@example.com about /Users/anna/x", LastError(device).Props().Str("message"));
        }
    }
}
