using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ManalensSdk;
using Xunit;

namespace Manalens.Tests
{
    /// <summary>Traits() (P-2814) and LinkSteam() (P-2815) with a mocked transport.</summary>
    public class TraitsAndLinkTests
    {
        static readonly byte[] Ticket = { 20, 0, 0, 0, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25 };
        const string TicketHex = "140000000a0b0c0d0e0f10111213141516171819";

        static List<JsonElement> Traits(Device device) => device.Transport.Requests.Where(r => r.Url.EndsWith("/v0/events")).SelectMany(r => r.Events).Where(e => e.Str("type") == "traits").ToList();
        static List<Request> Links(Device device) => device.Transport.Requests.Where(r => r.Url.EndsWith("/v0/link")).ToList();

        static (Device device, AnalyticsClient client, List<(bool, int, string)> results) Linker(System.Action<ManalensOptions> change = null)
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => { o.FlushAt = 50; change?.Invoke(o); }));
            var results = new List<(bool, int, string)>();
            client.SteamLinked += (linked, status, code) => results.Add((linked, status, code));
            client.Update(); // Sends game_open and session_start first, so the queued statuses answer the link.
            return (device, client, results);
        }

        [Fact]
        public void TraitsSendsATraitsEventWithTheAnonymousIdThenTheUserId()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.FlushAt = 50));
            client.Traits(new Dictionary<string, object> { ["level"] = 3, ["difficulty"] = "hard", ["note"] = null });
            client.Identify("player_7");
            client.Traits(new Dictionary<string, object> { ["level"] = 4 });
            client.SendNow();
            var sent = Traits(device);
            Assert.Equal(2, sent.Count);
            Assert.False(sent[0].TryGetProperty("event", out _));
            Assert.False(sent[0].TryGetProperty("user_id", out _));
            Assert.Equal(client.AnonymousId, sent[0].Str("anonymous_id"));
            Assert.Equal("app", sent[0].Str("channel"));
            Assert.Equal("{\"level\":3,\"difficulty\":\"hard\",\"note\":null}", sent[0].GetProperty("traits").GetRawText());
            Assert.Equal("player_7", sent[1].Str("user_id"));
        }

        [Fact]
        public void TraitsDropsProtectedTraitsWithOneWarning()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.FlushAt = 50));
            client.Traits(new Dictionary<string, object> { ["email"] = "ana@example.com", ["itch_user_id"] = "9", ["plan"] = "pro" });
            client.Traits(new Dictionary<string, object> { ["stripe_customer_id"] = "cus_1", ["paddle_customer_id"] = "ctm_1" });
            client.Traits(new Dictionary<string, object>());
            client.SendNow();
            var sent = Traits(device);
            Assert.Single(sent);
            Assert.Equal("{\"plan\":\"pro\"}", sent[0].GetProperty("traits").GetRawText());
            Assert.DoesNotContain(device.Transport.Requests, r => r.Body.Contains("ana@example.com"));
            Assert.Single(device.Log.Lines.Where(line => line.Contains("protected traits")));
        }

        [Fact]
        public void TraitsWaitForConsentAndAreForgottenAfterWithdrawalOrReset()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => { o.FlushAt = 50; o.ConsentRequired = true; }));
            client.Traits(new Dictionary<string, object> { ["level"] = 1, ["mode"] = "easy" });
            client.Traits(new Dictionary<string, object> { ["level"] = 2 });
            Assert.Equal(0, client.QueueSize);
            client.SetConsent(true);
            client.SendNow();
            var sent = Traits(device);
            Assert.Single(sent);
            Assert.Equal(2, sent[0].GetProperty("traits").GetProperty("level").GetInt32());
            Assert.Equal("easy", sent[0].GetProperty("traits").GetProperty("mode").GetString());
            client.SetConsent(false);
            client.Traits(new Dictionary<string, object> { ["level"] = 3 });
            client.Reset();
            client.SetConsent(true);
            client.SendNow();
            Assert.Single(Traits(device));
        }

        [Fact]
        public void SteamLinkIsReadyOnly20SecondsAfterTheFirstAcceptedBatch()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => { o.FlushAt = 50; o.SteamLink = true; }));
            Assert.False(client.SteamLinkReady);
            device.Transport.Statuses.Enqueue(503);
            client.SendNow();
            device.Clock.Advance(60);
            Assert.False(client.SteamLinkReady); // A failed batch does not count.
            client.Track("level_start");
            client.SendNow(); // 202
            Assert.False(client.SteamLinkReady);
            device.Clock.Advance(AnalyticsClient.SteamLinkDelaySeconds - 1);
            Assert.False(client.SteamLinkReady);
            device.Clock.Advance(1);
            Assert.True(client.SteamLinkReady);
            Assert.Equal(20, AnalyticsClient.SteamLinkDelaySeconds);
        }

        [Fact]
        public void LinkSteamPostsTheHexTicketAndTheAnonymousIdToTheApiHost()
        {
            var (device, client, results) = Linker(o => o.Endpoint = "https://events.manalens.dev");
            client.LinkSteam(Ticket);
            Assert.Empty(Links(device)); // Sent from Update(), on the transport's thread.
            client.Update();
            var links = Links(device);
            Assert.Single(links);
            Assert.Equal("https://api.manalens.dev/v0/link", links[0].Url);
            Assert.Equal(Device.Key, links[0].Headers["x-publishable-key"]);
            Assert.Equal("{\"provider\":\"steam\",\"ticket\":\"" + TicketHex + "\",\"anonymous_id\":\"" + client.AnonymousId + "\"}", links[0].Body);
            Assert.Equal(new[] { (true, 202, "") }, results);
            client.Update();
            Assert.Single(Links(device));
        }

        [Fact]
        public void LinkSteamUrlFollowsApiEndpointAndOtherHosts()
        {
            Assert.Equal("https://collect.example.com/v0/link", Linker().client.LinkSteamUrl);
            Assert.Equal("https://example.com/mana/v0/link", Linker(o => { o.Endpoint = "https://events.manalens.dev"; o.ApiEndpoint = "https://example.com/mana/"; }).client.LinkSteamUrl);
        }

        [Fact]
        public void LinkSteamWaitsForConsentAndRefusesABadTicket()
        {
            var (device, client, results) = Linker(o => o.ConsentRequired = true);
            client.LinkSteam(new byte[] { 1, 2, 3 });
            Assert.False(client.IsLinking);
            client.LinkSteam(Ticket);
            client.Update();
            Assert.Empty(Links(device));
            client.SetConsent(true);
            client.Update();
            Assert.Single(Links(device));
        }

        [Theory]
        [InlineData(400, "STEAM_TICKET_INVALID")]
        [InlineData(401, "UNAUTHORIZED")]
        [InlineData(403, "FORBIDDEN")]
        [InlineData(409, "STEAM_NOT_CONNECTED")]
        [InlineData(502, "STEAM_UNAVAILABLE")]
        [InlineData(503, "NOT_CONFIGURED")]
        public void LinkSteamDoesNotSendARefusalAgain(int status, string code)
        {
            var (device, client, results) = Linker();
            device.Transport.Statuses.Enqueue(status);
            device.Transport.ResponseBody = "{\"error\":{\"code\":\"" + code + "\",\"message\":\"no\"}}";
            client.LinkSteam(Ticket);
            for (var i = 0; i < 3; i++) { device.Clock.Advance(120); client.Update(); }
            Assert.Single(Links(device));
            Assert.Equal(new[] { (false, status, code) }, results);
        }

        [Fact]
        public void LinkSteamTriesOnceMoreAfterA429WithRetryAfterThenStops()
        {
            var (device, client, results) = Linker();
            device.Transport.Statuses.Enqueue(429);
            device.Transport.Statuses.Enqueue(429);
            device.Transport.RetryAfter = "2";
            device.Transport.ResponseBody = "{\"error\":{\"code\":\"RATE_LIMITED\"}}";
            client.LinkSteam(Ticket);
            client.Update();
            Assert.Single(Links(device));
            device.Clock.Advance(1);
            client.Update();
            Assert.Single(Links(device));
            device.Clock.Advance(1.5);
            client.Update();
            Assert.Equal(2, Links(device).Count);
            for (var i = 0; i < 3; i++) { device.Clock.Advance(120); client.Update(); }
            Assert.Equal(2, Links(device).Count);
            Assert.Equal(new[] { (false, 429, "RATE_LIMITED") }, results);
        }

        [Fact]
        public void LinkSteamDoesNotWaitForARetryAfterOver60Seconds()
        {
            var (device, client, results) = Linker();
            device.Transport.Statuses.Enqueue(429);
            device.Transport.RetryAfter = "120";
            client.LinkSteam(Ticket);
            client.Update();
            device.Clock.Advance(200);
            client.Update();
            Assert.Single(Links(device));
            Assert.Equal(429, results.Single().Item2);
        }

        [Fact]
        public void LinkSteamANetworkErrorIsNotSentAgain()
        {
            var (device, client, results) = Linker();
            device.Transport.Statuses.Enqueue(0);
            client.LinkSteam(Ticket);
            client.Update();
            device.Clock.Advance(120);
            client.Update();
            Assert.Single(Links(device));
            Assert.Equal(new[] { (false, 0, "NETWORK_ERROR") }, results);
        }

        [Fact]
        public void LinkSteamConsentWithdrawnDuringTheWaitSendsNoSecondRequest()
        {
            var (device, client, results) = Linker();
            device.Transport.Statuses.Enqueue(429);
            device.Transport.RetryAfter = "2";
            client.LinkSteam(Ticket);
            client.Update();
            client.SetConsent(false);
            device.Clock.Advance(3);
            client.Update();
            Assert.Single(Links(device));
            Assert.Equal(new[] { (false, 0, "CONSENT_REQUIRED") }, results);
        }

        [Fact]
        public void LinkSteamResetDuringTheWaitSendsNoSecondRequest()
        {
            var (device, client, results) = Linker();
            device.Transport.Statuses.Enqueue(429);
            device.Transport.RetryAfter = "2";
            client.LinkSteam(Ticket);
            client.Update();
            client.Reset();
            device.Clock.Advance(3);
            client.Update();
            Assert.Single(Links(device));
            Assert.Equal(new[] { (false, 0, "IDENTITY_CHANGED") }, results);
        }

        [Fact]
        public void LinkSteamDoesNothingWhenDisabled()
        {
            var (device, client, results) = Linker(o => o.Enabled = false);
            client.LinkSteam(Ticket);
            client.Update();
            Assert.Empty(device.Transport.Requests);
            Assert.Empty(results);
        }
    }
}
