using System.Linq;
using ManalensSdk;
using Xunit;

namespace Manalens.Tests
{
    /// <summary>Crash stacks (sdks/SPEC.md, P-2831): Unity's own crash data added to game_crash.</summary>
    public class CrashStackTests
    {
        static readonly string[] Homes = { "/Users/alex" };

        // A Mono macOS player after Utils.ForceCrash(AccessViolation), Unity 6000.6 (shortened).
        const string MacMono = @"probe start
Forcing a crash -- Intentionally Dereferencing NULL pointer

=================================================================
	Native Crash Reporting
=================================================================
Got a segv while executing native code. This usually indicates
a fatal error in the mono runtime or one of the native libraries
used by your application.
=================================================================

=================================================================
	Native stacktrace:
=================================================================
	0x12c764448 - /Users/alex/Games/Probe.app/Contents/Frameworks/libmonobdwgc-2.0.dylib : monovm_shutdown
	0x182d1d744 - /usr/lib/system/libsystem_platform.dylib : _sigtramp

=================================================================
	Managed Stacktrace:
=================================================================
	  at <unknown> <0xffffffff>
	  at UnityEngine.Diagnostics.Utils:ForceCrash <0x00007>
	  at CrashProbe:Crash <0x0000f>
	  at CrashProbe:Run <0x00053>
	  at System.Object:runtime_invoke_void <0x00067>
=================================================================
Obtained 22 stack frames.
#0  0x00000182cd85e8 in __pthread_kill
#1  0x00000182be2388 in raise
#2  0x00000105eb79c0 in MultiplyMatrixArrayWithBase4x4_NEON
#3  0x00000120394548 in  (wrapper managed-to-native) UnityEngine.Diagnostics.Utils:ForceCrash (UnityEngine.Diagnostics.ForcedCrashCategory) [{0xc360983b0} + 0x60]  (0x1203944e8 0x12039459c) [0x104c82d20 - Unity Root Domain]
#4  0x00000120394488 in  CrashProbe:Crash () [{0xc38443168} + 0x10]  (0x120394478 0x120394494) [0x104c82d20 - Unity Root Domain]
#20 0x000001058d1ebc in PlayerMain(int, char const**)
";

        // An IL2CPP Linux player (format of Unity's Linux crash handler).
        const string LinuxIl2Cpp = @"Caught fatal signal - signo:11 code:1 errno:0 addr:(nil)
Obtained 4 stack frames.
#0  0x007f3c2a1b2c3d in CrashProbe_Crash_m3F2A1B0C9D8E7F6A5B4C3D2E1F0A9B8C7D6E5F4A
#1  0x007f3c2a1b2d4e in CrashProbe_Run_m1A2B3C4D5E6F7A8B9C0D1E2F3A4B5C6D7E8F9A0B
#2  0x007f3c2a000111 in UnityPlayerMain
#3  0x007f3c29000222 in __libc_start_main
";

        // A Windows crash folder error.log.
        const string WindowsErrorLog = @"Unity Player [version: Unity 6000.6.10f1_40e81493c67d]

Probe.exe caused an Access Violation (0xc0000005)
  in module UnityPlayer.dll at 0033:2f1c4a10.

Error occurred at 2027-01-15_080025.
C:\Users\alex\Games\Probe\Probe.exe, run by alex.

Stack Trace of Crashed Thread 4321:
0x00007FFB2F1C4A10 (UnityPlayer) UnityMain
0x00007FFB2F1C4B20 (GameAssembly) CrashProbe_Crash_m3F2A1B0C9D8E7F6A5B4C3D2E1F0A9B8C7D6E5F4A
0x00007FFB2F1C4C30 (KERNEL32) BaseThreadInitThunk

Stack Trace of Thread 4322:
0x00007FFB30000000 (ntdll) NtWaitForSingleObject
";

        [Fact]
        public void AMonoMacCrashHasTheManagedFramesFirstThenTheNativeFrames()
        {
            var data = CrashStacks.Parse(MacMono, "unity_player_log", "macos", Homes);
            Assert.Equal("SIGSEGV", data["error_type"]);
            Assert.Equal("Got a segv while executing native code", data["message"]);
            Assert.Equal("unity_player_log", data["crash_source"]);
            Assert.Equal(true, data["symbolicated"]);
            // The collector refuses a whole event with a string over 1024 characters.
            Assert.All(data.Values, value => Assert.True(!(value is string text) || text.Length <= 1024));
            var frames = ((string)data["stack"]).Split('\n');
            Assert.Equal(new[] { "UnityEngine.Diagnostics.Utils:ForceCrash", "CrashProbe:Crash", "CrashProbe:Run" }, frames.Take(3));
            Assert.Contains("CrashProbe:Crash ()", frames);
            Assert.Contains("PlayerMain(int, char const**)", frames);
            Assert.DoesNotContain(frames, frame => frame.Contains("runtime_invoke") || frame.Contains("<unknown>") || frame.Contains("0x"));
            Assert.Equal(16, ((string)data["fingerprint"]).Length);
        }

        [Fact]
        public void TheFingerprintIgnoresAddressesAndOffsets()
        {
            var other = MacMono.Replace("<0x0000f>", "<0x00abc>").Replace("0x00000120394488", "0x00000999999999");
            Assert.Equal(CrashStacks.Parse(MacMono, "unity_player_log", "macos", Homes)["fingerprint"], CrashStacks.Parse(other, "unity_player_log", "macos", Homes)["fingerprint"]);
            var elsewhere = MacMono.Replace("CrashProbe:Crash <", "Inventory:Apply <");
            Assert.NotEqual(CrashStacks.Parse(MacMono, "unity_player_log", "macos", Homes)["fingerprint"], CrashStacks.Parse(elsewhere, "unity_player_log", "macos", Homes)["fingerprint"]);
        }

        [Fact]
        public void AnIl2CppLinuxCrashReadsTheSignalNumberAndCountsIl2CppNamesAsSymbols()
        {
            var data = CrashStacks.Parse(LinuxIl2Cpp, "unity_player_log", "linux", Homes);
            Assert.Equal("SIGSEGV", data["error_type"]);
            Assert.Equal("Caught fatal signal - signo:11 code:1 errno:0 addr:(nil)", data["message"]);
            Assert.StartsWith("CrashProbe_Crash_m", (string)data["stack"]);
            Assert.Equal(true, data["symbolicated"]);
            Assert.True(data.ContainsKey("fingerprint"));
            Assert.Equal("SIGBUS", CrashStacks.Parse(LinuxIl2Cpp.Replace("signo:11", "signo:7"), "unity_player_log", "linux", Homes)["error_type"]);
        }

        [Fact]
        public void AWindowsErrorLogHasTheExceptionAndOnlyTheCrashedThread()
        {
            var data = CrashStacks.Parse(WindowsErrorLog, "unity_crash_folder", "windows", new[] { @"C:\Users\alex" });
            Assert.Equal("EXCEPTION_ACCESS_VIOLATION", data["error_type"]);
            Assert.Equal("Probe.exe caused an Access Violation (0xc0000005)", data["message"]);
            Assert.Equal("(UnityPlayer) UnityMain\n(GameAssembly) CrashProbe_Crash_m3F2A1B0C9D8E7F6A5B4C3D2E1F0A9B8C7D6E5F4A\n(KERNEL32) BaseThreadInitThunk", data["stack"]);
            Assert.Equal(true, data["symbolicated"]);
        }

        [Fact]
        public void FramesWithoutNamesAreNotSymbolicated()
        {
            var data = CrashStacks.Parse("Caught fatal signal - signo:6\nObtained 2 stack frames.\n#0  0x007f3c2a1b2c3d in (Unknown)\n#1  0x007f3c2a1b2d4e in libunity.so\n", "unity_player_log", "linux", Homes);
            Assert.Equal("SIGABRT", data["error_type"]);
            Assert.Equal(false, data["symbolicated"]);
        }

        [Fact]
        public void HomeFoldersAreRemovedAndTheStackIsAtMost1024Characters()
        {
            var many = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"#{i}  0x00000120394488 in  /Users/alex/Games/Frame{i}:Method ()"));
            var data = CrashStacks.Parse("Caught fatal signal - signo:11\nObtained 400 stack frames.\n" + many + "\n", "unity_player_log", "linux", Homes);
            var stack = (string)data["stack"];
            Assert.True(stack.Length <= CrashStacks.MaxStack);
            Assert.DoesNotContain("/Users/alex", stack);
            Assert.StartsWith("~/Games/Frame0:Method ()", stack);
        }

        [Fact]
        public void ALogWithoutACrashGivesNothing()
        {
            Assert.Empty(CrashStacks.Parse("probe start\nThread 0x16fb77000 may have been prematurely finalized\n", "unity_player_log", "macos", Homes));
            Assert.Empty(CrashStacks.Parse("", "unity_player_log", "macos", Homes));
        }

        // The client: which file, once, consent, the option.

        static FakeCrashFiles CrashAfter(Device device, double seconds)
        {
            var files = new FakeCrashFiles();
            files.Add("/logs/Player-prev.log", device.Clock.Now + seconds, "unity_player_log", MacMono);
            return files;
        }

        static AnalyticsClient CrashThenLaunch(Device device, FakeCrashFiles files, ManalensOptions options = null, string platform = "windows")
        {
            var crashed = device.Launch(platform: platform);
            crashed.Update();
            device.Clock.Advance(25);
            crashed.Update();
            device.Clock.Advance(600);
            device.CrashFiles = files;
            var next = device.Launch(options, platform: platform);
            next.Update();
            return next;
        }

        [Fact]
        public void TheNextLaunchAddsTheCrashDataToGameCrash()
        {
            var device = new Device();
            var files = CrashAfter(device, 20);
            CrashThenLaunch(device, files);
            var crash = device.Transport.AllEvents.Single(e => e.Name() == "game_crash").Props();
            Assert.Equal("SIGSEGV", crash.Str("error_type"));
            Assert.Equal("unity_player_log", crash.Str("crash_source"));
            Assert.StartsWith("UnityEngine.Diagnostics.Utils:ForceCrash\nCrashProbe:Crash", crash.Str("stack"));
            Assert.Equal(16, crash.Str("fingerprint").Length);
            Assert.True(crash.GetProperty("symbolicated").GetBoolean());
            Assert.Equal("571", crash.Str("previous_build"));
        }

        [Fact]
        public void AFileFromBeforeTheCrashedSessionIsNotRead()
        {
            var device = new Device();
            var files = CrashAfter(device, -3600);
            CrashThenLaunch(device, files);
            var crash = device.Transport.AllEvents.Single(e => e.Name() == "game_crash").Props();
            Assert.False(crash.TryGetProperty("stack", out _));
            Assert.Equal(0, files.Reads);
        }

        [Fact]
        public void TheSameCrashFileIsNeverSentTwice()
        {
            var device = new Device();
            // The file time is after the start of both crashed sessions below,
            // so only the saved path and time keep it from being sent again.
            var files = CrashAfter(device, 625.5);
            var second = CrashThenLaunch(device, files);
            Assert.True(device.Transport.AllEvents.Single(e => e.Name() == "game_crash").Props().TryGetProperty("stack", out _));
            // The second launch crashes too (a force quit): no new crash data.
            device.Clock.Advance(25);
            second.Update();
            device.Clock.Advance(600);
            device.Launch().Update();
            var crash = device.Transport.AllEvents.Single(e => e.Name() == "game_crash").Props();
            Assert.False(crash.TryGetProperty("stack", out _));
            Assert.Equal(1, files.Reads);
        }

        [Fact]
        public void CaptureCrashStacksOffReadsNothing()
        {
            var device = new Device();
            var files = CrashAfter(device, 20);
            CrashThenLaunch(device, files, Device.Options(o => o.CaptureCrashStacks = false));
            var crash = device.Transport.AllEvents.Single(e => e.Name() == "game_crash").Props();
            Assert.False(crash.TryGetProperty("stack", out _));
            Assert.Equal(0, files.Reads);
        }

        [Fact]
        public void MobilePlayersReadNothing()
        {
            var device = new Device();
            var files = CrashAfter(device, 20);
            CrashThenLaunch(device, files, platform: "android");
            Assert.Equal(0, files.Reads);
        }

        [Fact]
        public void NothingIsReadBeforeConsent()
        {
            var device = new Device();
            var files = CrashAfter(device, 20);
            var crashed = device.Launch();
            crashed.Update();
            device.Clock.Advance(25);
            crashed.Update();
            device.Clock.Advance(600);
            device.CrashFiles = files;
            // Consent withdrawn in the meantime: the next launch records nothing.
            device.Launch(Device.Options(o => o.ConsentRequired = true)).Update();
            Assert.Equal(0, files.Reads);
        }
    }
}
