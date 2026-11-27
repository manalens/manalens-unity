using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ManalensSdk
{
    /// <summary>
    /// A file that can hold the crash data of an earlier run (sdks/SPEC.md
    /// "Crash stacks"): a Unity crash folder's error.log on Windows, or the
    /// previous Player log (Player-prev.log) on Windows, macOS, and Linux.
    /// </summary>
    public sealed class CrashFile
    {
        public string Path;
        /// <summary>The last write time, Unix seconds.</summary>
        public double ModifiedUnixSeconds;
        /// <summary>unity_crash_folder or unity_player_log.</summary>
        public string Source;
        /// <summary>Reads at most the last <c>maxBytes</c> of the file; null when it cannot be read.</summary>
        public Func<int, string> ReadTail;
    }

    /// <summary>The crash files of this game, newest and best first. The Unity layer finds them; tests use a fake.</summary>
    public interface ICrashFiles
    {
        IEnumerable<CrashFile> Candidates();
    }

    /// <summary>
    /// Reads the crash data that Unity itself wrote when the game crashed. The
    /// SDK installs no crash handler: at the next launch it parses the crash
    /// text and adds stack, error_type, message, fingerprint, crash_source and
    /// symbolicated to app_crash.
    /// </summary>
    public static class CrashStacks
    {
        public const int TailBytes = 262144;
        /// <summary>The collector refuses a whole event with a property string over 1024 characters.</summary>
        public const int MaxStack = 1024;
        public const int MaxMessage = 1024;
        const int FingerprintFrames = 5;

        static readonly Regex GotSignalRe = new Regex(@"Got a (\w+) while executing native code", RegexOptions.Compiled);
        static readonly Regex ReceivedSignalRe = new Regex(@"Received signal (SIG[A-Z]+)", RegexOptions.Compiled);
        static readonly Regex SignoRe = new Regex(@"signo:\s*(\d+)", RegexOptions.Compiled);
        static readonly Regex WindowsCodeRe = new Regex(@"caused an? (.+?) \((0x[0-9a-fA-F]{8})\)", RegexOptions.Compiled);
        static readonly Regex NativeFrameRe = new Regex(@"^#\d+\s+0x[0-9a-fA-F]+ in\s+(.+)$", RegexOptions.Compiled);
        static readonly Regex WindowsFrameRe = new Regex(@"^0x[0-9a-fA-F]+\s+(.+)$", RegexOptions.Compiled);
        static readonly Regex ManagedFrameRe = new Regex(@"^at\s+(.+?)(\s+<0x[0-9a-fA-F]+>)?$", RegexOptions.Compiled);
        static readonly Regex NoiseRe = new Regex(@"0x[0-9a-fA-F]+|<[^>]*>|\[[^\]]*\]|\{[^}]*\}|\+\s*[0-9a-fA-Fx]+|:\d+\b|\s+", RegexOptions.Compiled);
        static readonly Regex SourceLineRe = new Regex(@"\.(cpp|cc|c|h|mm|cs):\d+", RegexOptions.Compiled);
        static readonly Regex Il2CppMethodRe = new Regex(@"_m[0-9A-F]{8,}", RegexOptions.Compiled);

        static readonly Dictionary<int, string> Signals = new Dictionary<int, string>
        {
            [4] = "SIGILL", [5] = "SIGTRAP", [6] = "SIGABRT", [8] = "SIGFPE", [11] = "SIGSEGV",
        };
        static readonly Dictionary<string, string> WindowsCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["0xc0000005"] = "EXCEPTION_ACCESS_VIOLATION",
            ["0xc00000fd"] = "EXCEPTION_STACK_OVERFLOW",
            ["0xc0000094"] = "EXCEPTION_INT_DIVIDE_BY_ZERO",
            ["0xc000001d"] = "EXCEPTION_ILLEGAL_INSTRUCTION",
            ["0xc0000409"] = "EXCEPTION_STACK_BUFFER_OVERRUN",
            ["0x80000003"] = "EXCEPTION_BREAKPOINT",
        };
        /// <summary>Frames of the crash machinery, left out of the fingerprint.</summary>
        static readonly string[] HandlerFrames = { "_sigtramp", "pthread_kill", "raise", "abort", "Unknown", "mono_unity_backtrace", "mono_jit_set_domain", "monovm_shutdown" };

        /// <summary>
        /// Parses the crash text of one run. Returns an empty dictionary when
        /// the text has no crash (a force quit or a power loss).
        /// </summary>
        public static Dictionary<string, object> Parse(string text, string source, string platform, IEnumerable<string> homeDirs)
        {
            var result = new Dictionary<string, object>();
            if (string.IsNullOrEmpty(text)) return result;
            var lines = text.Replace("\r\n", "\n").Split('\n');
            string errorType = null, message = null;
            var managed = new List<string>();
            var native = new List<string>();
            var section = "";
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (message == null && IsCrashLine(line)) message = FirstSentence(line);
                if (errorType == null) errorType = CrashType(line, platform);
                if (line.StartsWith("Managed Stacktrace:", StringComparison.Ordinal)) { section = "managed"; continue; }
                if (line.StartsWith("Obtained ", StringComparison.Ordinal) && line.EndsWith("stack frames.", StringComparison.Ordinal)) { section = "native"; continue; }
                if (line.Contains("OUTPUTTING STACK TRACE") || line.StartsWith("Stack Trace of Crashed Thread", StringComparison.Ordinal)) { section = "windows"; continue; }
                if (line.Contains("END OF STACKTRACE") || (section == "windows" && line == "")) { section = ""; continue; }
                if (section == "managed")
                {
                    if (line.StartsWith("=====", StringComparison.Ordinal)) { if (managed.Count > 0) section = ""; continue; }
                    var m = ManagedFrameRe.Match(line);
                    if (!m.Success) continue;
                    var frame = m.Groups[1].Value;
                    if (frame == "<unknown>" || frame.Contains(":runtime_invoke_")) continue;
                    managed.Add(frame);
                }
                else if (section == "native")
                {
                    var m = NativeFrameRe.Match(line);
                    if (!m.Success) { if (line != "") section = ""; continue; }
                    var frame = m.Groups[1].Value.Trim();
                    var bracket = frame.IndexOf(" [", StringComparison.Ordinal);
                    native.Add(bracket > 0 ? frame.Substring(0, bracket).Trim() : frame);
                }
                else if (section == "windows")
                {
                    var m = WindowsFrameRe.Match(line);
                    if (m.Success) native.Add(m.Groups[1].Value.Trim());
                }
            }
            if (message == null && managed.Count == 0 && native.Count == 0) return result;
            var homes = homeDirs ?? new List<string>();
            result["error_type"] = errorType ?? "crash";
            result["message"] = Truncate(ErrorTools.CleanLogMessage(message ?? "The game crashed", homes), MaxMessage);
            result["crash_source"] = source;
            for (var i = 0; i < managed.Count; i++) managed[i] = ErrorTools.CleanStackFrame(managed[i], homes);
            for (var i = 0; i < native.Count; i++) native[i] = ErrorTools.CleanStackFrame(native[i], homes);
            var frames = new List<string>(managed);
            frames.AddRange(native);
            if (frames.Count == 0) return result;
            var stack = "";
            foreach (var frame in frames)
            {
                var next = stack == "" ? frame : stack + "\n" + frame;
                if (next.Length > MaxStack) break;
                stack = next;
            }
            result["stack"] = stack;
            var symbolicated = managed.Count > 0;
            foreach (var frame in native)
                if (SourceLineRe.IsMatch(frame) || Il2CppMethodRe.IsMatch(frame)) { symbolicated = true; break; }
            result["symbolicated"] = symbolicated;
            var basis = (string)result["error_type"];
            var count = 0;
            foreach (var frame in managed.Count > 0 ? managed : native)
            {
                if (count == FingerprintFrames) break;
                if (managed.Count == 0 && IsHandlerFrame(frame)) continue;
                basis += "|" + NoiseRe.Replace(frame, "");
                count++;
            }
            if (count > 0) result["fingerprint"] = Ids.Sha1Prefix(basis);
            return result;
        }

        /// <summary>A desktop platform where Unity writes crash data the SDK can read.</summary>
        public static bool Supported(string platform) => platform == "windows" || platform == "macos" || platform == "linux";

        static bool IsCrashLine(string line) =>
            line.StartsWith("Got a ", StringComparison.Ordinal) || line.StartsWith("Received signal", StringComparison.Ordinal)
            || line.StartsWith("Caught fatal signal", StringComparison.Ordinal) || WindowsCodeRe.IsMatch(line) || line == "Crash!!!";

        static string CrashType(string line, string platform)
        {
            var m = GotSignalRe.Match(line);
            if (m.Success)
            {
                var name = m.Groups[1].Value.ToUpperInvariant();
                return name.StartsWith("SIG", StringComparison.Ordinal) ? name : "SIG" + name;
            }
            m = ReceivedSignalRe.Match(line);
            if (m.Success) return m.Groups[1].Value;
            m = SignoRe.Match(line);
            if (m.Success)
            {
                var number = int.Parse(m.Groups[1].Value);
                if (number == (platform == "macos" ? 10 : 7)) return "SIGBUS";
                return Signals.TryGetValue(number, out var name) ? name : "signal " + number;
            }
            m = WindowsCodeRe.Match(line);
            if (m.Success) return WindowsCodes.TryGetValue(m.Groups[2].Value, out var code) ? code : m.Groups[2].Value.ToLowerInvariant();
            return null;
        }

        static string FirstSentence(string line)
        {
            var end = line.IndexOf(". ", StringComparison.Ordinal);
            return end > 0 ? line.Substring(0, end) : line.TrimEnd('.');
        }

        static bool IsHandlerFrame(string frame)
        {
            foreach (var name in HandlerFrames)
                if (frame.Contains(name)) return true;
            return false;
        }

        static string Truncate(string text, int max) => text.Length <= max ? text : text.Substring(0, max);
    }
}
