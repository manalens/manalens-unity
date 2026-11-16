using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using UntitledAnalyticsSdk;
using Xunit;

namespace UntitledAnalytics.Tests
{
    /// <summary>
    /// The contract with the collector. A fixed clock and fixed IDs make the
    /// request bodies the same on every run. They are kept in
    /// Tests~/fixtures/*.json; tests/unity-sdk.test.mjs (pnpm test:unit) checks
    /// them against the collector schema (packages/shared ingestSchema).
    /// This test fails when the SDK output changes; run it with
    /// UPDATE_FIXTURES=1 to write the new files, then review the diff.
    /// </summary>
    public class FixtureTests
    {
        static string FixtureFolder([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here), "..", "fixtures");

        static List<string> Scenario()
        {
            var device = new Device { Sequential = true };
            // Launch 1: plays, identifies, hits an error, then crashes (no quit).
            var first = device.Launch(Device.Options(o => o.FlushAt = 50));
            first.Track("level_start", new Dictionary<string, object> { ["level"] = 3, ["mode"] = "story" });
            first.Track("resource", new Dictionary<string, object>
            {
                ["flow"] = "source", ["currency"] = "gold", ["amount"] = 25, ["ratio"] = 0.75, ["float"] = 0.1f,
                ["big"] = 9_000_000_000L, ["price"] = 4.99m, ["boss"] = false, ["note"] = null, ["text"] = "quote \" slash \\ line\nend",
            });
            first.Identify("player_42", new Dictionary<string, object> { ["plan"] = "supporter", ["level"] = 12, ["vip"] = true });
            first.TrackError("Save failed for slot 3", new TrackErrorOptions { Severity = "critical", ErrorType = "SaveFailed", Handled = false, Properties = new Dictionary<string, object> { ["slot"] = 3 } });
            device.Clock.Advance(5);
            first.CaptureLog("NullReferenceException: Object reference not set to an instance of an object", "Game.Player.Jump () (at Assets/Scripts/Player.cs:42)\nGame.Player.Update () (at Assets/Scripts/Player.cs:12)", LogKind.Exception);
            first.Update();
            device.Clock.Advance(10);
            first.Update(); // The tick saves the session; then the process dies.
            var bodies = device.Transport.Requests.Select(r => r.Body).ToList();
            // Launch 2: app_unclean_exit and session_end of the crashed session, then a clean shutdown.
            device.Clock.Advance(120);
            var second = device.Launch(Device.Options(o => { o.Build = "572"; o.Storefront = "itch"; o.AppVersion = "1.4.1-beta.2+572"; }));
            second.Track("level_complete", new Dictionary<string, object> { ["level"] = 3, ["score"] = 1200 });
            device.Clock.Advance(42);
            second.BeginShutdown();
            bodies.AddRange(device.Transport.Requests.Select(r => r.Body));
            return bodies;
        }

        [Fact]
        public void RequestBodiesMatchTheCommittedFixtures()
        {
            var bodies = Scenario();
            Assert.Equal(Scenario(), bodies); // Deterministic.
            var folder = FixtureFolder();
            var expected = bodies.Select((body, i) => (name: $"request-{i + 1}.json", body)).ToList();
            if (Environment.GetEnvironmentVariable("UPDATE_FIXTURES") == "1")
            {
                Directory.CreateDirectory(folder);
                foreach (var old in Directory.GetFiles(folder, "request-*.json")) File.Delete(old);
                foreach (var (name, body) in expected) File.WriteAllText(Path.Combine(folder, name), body + "\n");
            }
            var committed = Directory.Exists(folder) ? Directory.GetFiles(folder, "request-*.json").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToList() : new List<string>();
            Assert.Equal(expected.Select(e => e.name).OrderBy(n => n, StringComparer.Ordinal), committed);
            foreach (var (name, body) in expected)
                Assert.True(File.ReadAllText(Path.Combine(folder, name)) == body + "\n", $"{name} changed; run with UPDATE_FIXTURES=1 and review the diff");
        }
    }
}
