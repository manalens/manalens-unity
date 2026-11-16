using System.Linq;
using UntitledAnalyticsSdk;
using Xunit;

namespace UntitledAnalytics.Tests
{
    /// <summary>An opt-out must survive a lost, broken or unreadable state file (fail closed).</summary>
    public class ConsentSafetyTests
    {
        static Device OptedOutDevice()
        {
            var device = new Device();
            var client = device.Launch();
            client.Update();
            client.SetConsent(false);
            Assert.True(device.Storage.Blobs.ContainsKey(AnalyticsClient.OptOutName));
            return device;
        }

        static void AssertSilent(Device device, AnalyticsClient client)
        {
            client.Track("after_restart");
            client.TrackError("after restart");
            client.Update();
            device.Clock.Advance(20);
            client.Update();
            client.OnQuit();
            Assert.False(client.IsActive);
            Assert.Equal("", client.AnonymousId);
            Assert.Empty(device.Transport.Requests);
        }

        [Fact]
        public void ACorruptStateAfterAnOptOutStaysOffAndIsNotOverwritten()
        {
            var device = OptedOutDevice();
            const string broken = "# untitled_analytics state v1\nconsent.granted=fal";
            device.Storage.Blobs[AnalyticsClient.StateName] = broken;
            device.Storage.Blobs.Remove(AnalyticsClient.OptOutName); // Even without the marker.
            var client = device.Launch(Device.Options(o => o.ConsentRequired = false));
            AssertSilent(device, client);
            Assert.Equal(broken, device.Storage.Blobs[AnalyticsClient.StateName]);
            Assert.Equal(broken, device.Storage.Blobs[AnalyticsClient.CorruptStateName]);
            Assert.True(device.Log.Has("the saved state is damaged"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("consent.granted=true\n")] // No header: not written by this SDK.
        [InlineData("# untitled_analytics state v1\nconsent.granted=true\n")] // No end line: cut off.
        [InlineData("# untitled_analytics state v1\nno equals sign\n# end\n")]
        public void AnyUnparsableStateIsTreatedAsUnknown(string text)
        {
            var device = new Device();
            device.Storage.Blobs[AnalyticsClient.StateName] = text;
            var client = device.Launch();
            AssertSilent(device, client);
            Assert.Equal(text, device.Storage.Blobs[AnalyticsClient.StateName]);
        }

        [Fact]
        public void AMissingStateIsAFirstRun()
        {
            var device = new Device();
            var client = device.Launch();
            client.Update();
            Assert.True(client.IsActive);
            Assert.Equal(new[] { "session_start", "game_open" }, device.Transport.Names);
            Assert.True(StateFile.TryParse(device.Storage.Blobs[AnalyticsClient.StateName], out var state));
            Assert.Equal(client.AnonymousId, state.Get("identity.anonymous_id"));
        }

        [Fact]
        public void AReadErrorStaysOffAndKeepsTheFile()
        {
            var device = new Device();
            var first = device.Launch();
            first.Update();
            var saved = device.Storage.Blobs[AnalyticsClient.StateName];
            device.Storage.FailReads.Add(AnalyticsClient.StateName);
            var client = device.Launch();
            AssertSilent(device, client);
            client.SetConsent(true); // Not even an explicit grant: the state is unknown.
            client.Update();
            Assert.False(client.IsActive);
            Assert.Empty(device.Transport.Requests);
            Assert.Equal(saved, device.Storage.Blobs[AnalyticsClient.StateName]);
            Assert.True(device.Log.Has("could not read the saved state"));
        }

        [Fact]
        public void TheOptOutMarkerWinsOverTheState()
        {
            var device = OptedOutDevice();
            // A state that says "granted" (for example restored from a backup) does not undo the opt-out.
            var state = new StateFile();
            state.SetBool("consent.granted", true);
            device.Storage.Blobs[AnalyticsClient.StateName] = state.Serialize();
            var client = device.Launch();
            AssertSilent(device, client);
            // A marker that cannot be read counts as present.
            device.Storage.FailReads.Add(AnalyticsClient.OptOutName);
            var next = device.Launch();
            AssertSilent(device, next);
        }

        [Fact]
        public void SetConsentTrueClearsTheMarkerAndStartsTracking()
        {
            var device = OptedOutDevice();
            var client = device.Launch();
            Assert.False(client.IsActive);
            client.SetConsent(true);
            client.Update();
            Assert.True(client.IsActive);
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.OptOutName));
            Assert.Equal(new[] { "session_start", "game_open" }, device.Transport.Names);
            var next = device.Launch();
            Assert.True(next.IsActive);
        }

        [Fact]
        public void SetConsentTrueStartsANewStateAfterACorruptOne()
        {
            var device = new Device();
            device.Storage.Blobs[AnalyticsClient.StateName] = "garbage";
            var client = device.Launch(Device.Options(o => o.ConsentRequired = true));
            Assert.False(client.IsActive);
            client.SetConsent(true);
            Assert.True(client.IsActive);
            Assert.Equal("garbage", device.Storage.Blobs[AnalyticsClient.CorruptStateName]);
            Assert.True(StateFile.TryParse(device.Storage.Blobs[AnalyticsClient.StateName], out _));
        }

        [Fact]
        public void AFailedOptOutWriteKeepsTheSdkOffAndIsRetried()
        {
            var device = new Device();
            var client = device.Launch();
            client.Update();
            device.Storage.FailWrites.Add(AnalyticsClient.OptOutName);
            device.Storage.FailWrites.Add(AnalyticsClient.StateName);
            client.SetConsent(false);
            Assert.True(device.Log.Has("could not save the opt-out"));
            client.Track("after_withdraw");
            device.Clock.Advance(11);
            client.Update(); // Retry fails again.
            Assert.False(client.IsActive);
            Assert.Single(device.Transport.Requests);
            device.Storage.FailWrites.Clear();
            device.Clock.Advance(11);
            client.Update(); // Retry succeeds.
            Assert.True(device.Storage.Blobs.ContainsKey(AnalyticsClient.OptOutName));
            Assert.Contains("consent.granted=false", device.Storage.Blobs[AnalyticsClient.StateName]);
            Assert.DoesNotContain("anonymous_id", device.Storage.Blobs[AnalyticsClient.StateName]);
            Assert.DoesNotContain(device.Transport.Requests, r => r.Body.Contains("after_withdraw"));
            var next = device.Launch();
            Assert.False(next.IsActive);
        }

        [Fact]
        public void SetConsentFalseBeforeInitIsAppliedAtInit()
        {
            var device = new Device();
            var client = device.Launch(init: false);
            client.SetConsent(false);
            Assert.True(client.Init());
            client.Update();
            Assert.False(client.IsActive);
            Assert.Empty(device.Transport.Requests);
            Assert.True(device.Storage.Blobs.ContainsKey(AnalyticsClient.OptOutName));
        }
    }
}
