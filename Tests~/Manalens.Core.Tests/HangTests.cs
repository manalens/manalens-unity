using System.Linq;
using System.Text.Json;
using ManalensSdk;
using Xunit;

namespace Manalens.Tests
{
    /// <summary>
    /// Hang detection (P-2833): the watchdog sees a stuck main thread, writes the
    /// hang while it is stuck, and the SDK sends one game_hang error. A crash
    /// after it links to it with hang_message_id. The tests run the watchdog
    /// checks by hand (CheckHang) with the fake clock, as its thread would.
    /// </summary>
    public class HangTests
    {
        static AnalyticsClient Warm(Device device, ManalensOptions options = null)
        {
            var client = device.Launch(options ?? Device.Options(o => o.FlushAt = 50));
            for (var i = 0; i <= HangWatchdog.WarmupFrames; i++) client.Update();
            return client;
        }

        /// <summary>The main thread is stuck: no Update(), the watchdog checks every 250 ms.</summary>
        static void Stall(Device device, AnalyticsClient client, double seconds)
        {
            for (var t = 0.0; t < seconds; t += 0.25)
            {
                device.Clock.Advance(0.25);
                client.CheckHang();
            }
        }

        static JsonElement[] Hangs(Device device) =>
            device.Transport.AllEvents.Where(e => e.Name() == "error" && e.Props().Str("error_type") == "game_hang").ToArray();

        [Fact]
        public void AStuckMainThreadIsWrittenWhileStuckAndSentOnceWhenItRecovers()
        {
            var device = new Device();
            var client = Warm(device);
            client.Track("before_hang");
            var stuckAt = device.Clock.Now;
            Stall(device, client, 6);
            // Written while the main thread is stuck, before any Update().
            Assert.True(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
            Assert.Empty(Hangs(device));
            client.Update();
            client.SendNow();
            var hang = Assert.Single(Hangs(device));
            var props = hang.Props();
            Assert.Equal("error", props.Str("severity"));
            Assert.Equal("auto", props.Str("source"));
            Assert.False(props.GetProperty("handled").GetBoolean());
            Assert.Equal(6.0, props.GetProperty("hang_duration_s").GetDouble());
            Assert.Equal("The main thread was blocked for 6.0 s", props.Str("message"));
            Assert.False(props.TryGetProperty("stack", out _)); // Unity cannot read the main thread stack from another thread.
            Assert.Contains("before_hang", props.Str("breadcrumbs"));
            Assert.Equal(Ids.Iso(stuckAt), hang.Str("original_timestamp"));
            Assert.Equal(client.SessionId, hang.Str("session_id"));
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
            Assert.Contains("session.hang_message_id=" + hang.Str("message_id"), device.Storage.Blobs[AnalyticsClient.StateName]);

            // The next frames: no second hang for the same stuck period.
            client.Update();
            client.SendNow();
            Assert.Single(Hangs(device));
        }

        [Fact]
        public void HangDurationIsInSecondsWithOneDecimal()
        {
            var device = new Device();
            var client = Warm(device);
            Stall(device, client, 6.2); // 25 checks of 0.25 s: 6.25 s
            client.Update();
            client.SendNow();
            var props = Assert.Single(Hangs(device)).Props();
            Assert.Equal(6.3, props.GetProperty("hang_duration_s").GetDouble());
            Assert.Equal("The main thread was blocked for 6.3 s", props.Str("message"));
        }

        [Fact]
        public void AGameKilledWhileStuckSendsTheHangAtTheNextLaunchAndTheCrashLinksToIt()
        {
            var device = new Device();
            var client = Warm(device);
            var session = client.SessionId;
            client.Track("before_hang");
            Stall(device, client, 8);
            // The player kills the game: no Update(), no OnQuit().
            device.Clock.Advance(60);
            var next = device.Launch(Device.Options(o => o.Build = "572"));
            next.Update();
            var hang = Assert.Single(Hangs(device));
            var crash = device.Transport.AllEvents.Single(e => e.Name() == "game_crash");
            Assert.Equal(hang.Str("message_id"), crash.Props().Str("hang_message_id"));
            Assert.Equal(session, hang.Str("session_id"));
            Assert.Equal(session, crash.Props().Str("previous_session_id"));
            Assert.False(hang.Props().GetProperty("handled").GetBoolean());
            Assert.Equal("error", hang.Props().Str("severity"));
            Assert.False(hang.Props().TryGetProperty("hang_duration_s", out _));
            Assert.Equal("The main thread was blocked for 5.0 s", hang.Props().Str("message")); // The threshold: the hang never ended.
            Assert.Contains("before_hang", hang.Props().Str("breadcrumbs"));
            Assert.Equal("571", hang.GetProperty("context").GetProperty("app").Str("build")); // The build of the run that hung.
            Assert.Equal(new[] { "error", "game_crash", "session_end", "session_start", "game_open" }, device.Transport.Names);
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
        }

        [Fact]
        public void ACrashLaterInTheSessionLinksToTheLastRecoveredHang()
        {
            var device = new Device();
            var client = Warm(device);
            Stall(device, client, 6);
            client.Update();
            client.SendNow();
            var hang = Assert.Single(Hangs(device));
            device.Clock.Advance(20);
            client.Update(); // A tick saves the session.
            device.Clock.Advance(60);
            device.Launch().Update(); // The process died without OnQuit.
            var crash = device.Transport.AllEvents.Single(e => e.Name() == "game_crash");
            Assert.Equal(hang.Str("message_id"), crash.Props().Str("hang_message_id"));
        }

        [Fact]
        public void ACrashWithoutAHangHasNoHangMessageId()
        {
            var device = new Device();
            var client = Warm(device);
            device.Clock.Advance(20);
            client.Update();
            device.Clock.Advance(60);
            device.Launch().Update();
            var crash = device.Transport.AllEvents.Single(e => e.Name() == "game_crash");
            Assert.False(crash.Props().TryGetProperty("hang_message_id", out _));
        }

        [Fact]
        public void NoHangInTheFirstFramesWhileUnfocusedOrWhileTheEditorIsPaused()
        {
            var device = new Device();
            var client = device.Launch(Device.Options(o => o.FlushAt = 50));
            client.Update();
            Stall(device, client, 8); // A long first load.
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
            for (var i = 0; i <= HangWatchdog.WarmupFrames; i++) client.Update();

            client.OnFocusChanged(false);
            client.Update();
            Stall(device, client, 8);
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
            client.OnFocusChanged(true);
            client.Update();

            client.SetHangPaused(true);
            Stall(device, client, 8);
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
            client.SetHangPaused(false);
            client.Update();

            Stall(device, client, 6);
            Assert.True(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
        }

        [Fact]
        public void ASuspendedProcessIsNotAHang()
        {
            // System sleep or a mobile app in the background: the watchdog thread
            // itself did not run for a long time.
            var device = new Device();
            var client = Warm(device);
            client.CheckHang();
            device.Clock.Advance(3600);
            client.CheckHang();
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
        }

        [Fact]
        public void ZeroTurnsItOffAndWebGLHasNoWatchdog()
        {
            var off = new Device();
            var client = Warm(off, Device.Options(o => o.HangThresholdSeconds = 0));
            Stall(off, client, 10);
            Assert.False(off.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));

            var web = new Device();
            var page = web.Launch(platform: "web");
            for (var i = 0; i <= HangWatchdog.WarmupFrames; i++) page.Update();
            Stall(web, page, 10);
            Assert.False(web.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
        }

        [Fact]
        public void TheThresholdIsAnOption()
        {
            var device = new Device();
            var client = Warm(device, Device.Options(o => o.HangThresholdSeconds = 2));
            Stall(device, client, 1.5);
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
            Stall(device, client, 1);
            Assert.True(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
            client.Update();
            client.SendNow();
            Assert.Equal("The main thread was blocked for 2.5 s", Assert.Single(Hangs(device)).Props().Str("message"));
        }

        [Fact]
        public void TheThresholdIsClampedToOneToSixtySeconds()
        {
            Assert.Equal(1, new HangWatchdog(0.2, new FakeClock(), null).ThresholdSeconds);
            Assert.Equal(60, new HangWatchdog(600, new FakeClock(), null).ThresholdSeconds);
            Assert.Equal(2.5, new HangWatchdog(2.5, new FakeClock(), null).ThresholdSeconds);

            var device = new Device();
            var client = Warm(device, Device.Options(o => { o.FlushAt = 50; o.HangThresholdSeconds = 0.3; }));
            Stall(device, client, 0.75);
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
            Stall(device, client, 0.5);
            Assert.True(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
        }

        [Fact]
        public void FocusLossAndTheEditorPauseHaveSeparateFlags()
        {
            var device = new Device();
            var client = Warm(device);
            client.SetHangPaused(true);
            client.OnFocusChanged(false);
            client.OnFocusChanged(true); // Focus is back, the editor is still paused.
            client.Update();
            Stall(device, client, 8);
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));

            client.OnFocusChanged(false);
            client.SetHangPaused(false); // The editor runs again, the game is still unfocused.
            Stall(device, client, 8);
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));

            client.OnFocusChanged(true);
            client.Update();
            Stall(device, client, 6);
            Assert.True(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
        }

        [Fact]
        public void BeforeSendCanDropTheHangAndThenNoCrashLinksToIt()
        {
            var device = new Device();
            var client = Warm(device, Device.Options(o =>
            {
                o.FlushAt = 50;
                o.BeforeSend = e => e.TryGetValue("properties", out var p) && p is System.Collections.Generic.Dictionary<string, object> props
                    && props.TryGetValue("error_type", out var t) && (t as string) == "game_hang" ? null : e;
            }));
            Stall(device, client, 6);
            client.Update();
            client.SendNow();
            Assert.Empty(Hangs(device));
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
            Assert.DoesNotContain("hang_message_id", device.Storage.Blobs[AnalyticsClient.StateName]);
        }

        [Fact]
        public void HangsUseTheErrorLimits()
        {
            var device = new Device();
            var client = Warm(device, Device.Options(o => { o.FlushAt = 50; o.MaxAutoErrorsPerSession = 1; }));
            Stall(device, client, 6);
            client.Update();
            Stall(device, client, 6);
            client.Update();
            client.SendNow();
            Assert.Single(Hangs(device)); // The second automatic error is over the cap.
        }

        [Fact]
        public void ConsentWithdrawalRemovesASavedHang()
        {
            var device = new Device();
            var client = Warm(device);
            Stall(device, client, 6);
            Assert.True(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
            client.SetConsent(false);
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
        }

        [Fact]
        public void ConsentWithdrawalStopsTheCountAndForgetsTheSessionAndBreadcrumbs()
        {
            var device = new Device();
            var client = Warm(device);
            client.Track("before_withdrawal");
            client.Update();
            client.SetConsent(false);
            // The main thread gets stuck right after the withdrawal, before any Update().
            Stall(device, client, 8);
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
        }

        [Fact]
        public void ResetForgetsTheOldSessionAndBreadcrumbsInTheWatchdog()
        {
            var device = new Device();
            var client = Warm(device);
            var old = client.SessionId;
            client.Track("before_reset");
            client.Update();
            client.Reset();
            // Stuck right after Reset(), before any Update(): no hang with the old player's data.
            Stall(device, client, 8);
            Assert.False(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
            client.Update();
            Stall(device, client, 6);
            Assert.True(device.Storage.Blobs.ContainsKey(AnalyticsClient.HangName));
            var saved = device.Storage.Blobs[AnalyticsClient.HangName];
            Assert.DoesNotContain(old, saved);
            Assert.DoesNotContain("before_reset", saved);
        }

        [Fact]
        public void TheWatchdogDoesNotCountADebuggerPause()
        {
            var clock = new FakeClock();
            var stuck = 0;
            var watchdog = new HangWatchdog(5, clock, _ => stuck++) { DebuggerAttached = () => true };
            for (var i = 0; i <= HangWatchdog.WarmupFrames; i++) watchdog.Beat(true, "s");
            for (var i = 0; i < 40; i++)
            {
                clock.Advance(0.25);
                watchdog.Check();
            }
            Assert.Equal(0, stuck);
            watchdog.DebuggerAttached = () => false;
            for (var i = 0; i < 24; i++)
            {
                clock.Advance(0.25);
                watchdog.Check();
            }
            Assert.Equal(1, stuck);
        }

        [Fact]
        public void TheWatchdogThreadSeesARealStall()
        {
            var stuck = new System.Threading.ManualResetEventSlim();
            var watchdog = HangWatchdog.ForTests(0.2, new SystemClock(), _ => stuck.Set());
            watchdog.DebuggerAttached = () => false;
            for (var i = 0; i <= HangWatchdog.WarmupFrames; i++) watchdog.Beat(true, "s");
            watchdog.Start();
            try
            {
                Assert.True(stuck.Wait(5000), "the watchdog thread did not see the stuck main thread");
                var hang = watchdog.Beat(true, "s");
                Assert.NotNull(hang);
                Assert.True(hang.DurationMs >= 200);
            }
            finally
            {
                watchdog.Stop();
            }
        }
    }
}
