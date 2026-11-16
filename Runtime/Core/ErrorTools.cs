using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace UntitledAnalyticsSdk
{
    /// <summary>The first game frame of a stack trace.</summary>
    public sealed class StackFrameInfo
    {
        public string File = "";
        public int Line;
        public string Function = "";
    }

    /// <summary>Stack parsing and error grouping. Pure functions, no state.</summary>
    public static class ErrorTools
    {
        static readonly Regex NumberRe = new Regex("0x[0-9A-Fa-f]+|\\d+", RegexOptions.CultureInvariant);
        static readonly Regex QuotedRe = new Regex("\"[^\"]*\"|'[^']*'", RegexOptions.CultureInvariant);
        // Unity: "Game.Player.Jump () (at Assets/Scripts/Player.cs:42)".
        static readonly Regex UnityAtRe = new Regex("\\(at (.+):(\\d+)\\)\\s*$", RegexOptions.CultureInvariant);
        // .NET: "at Game.Player.Jump () [0x00000] in /src/Player.cs:42" or "... in C:\src\Player.cs:line 42".
        static readonly Regex DotNetInRe = new Regex(" in (.+?):(?:line )?(\\d+)\\s*$", RegexOptions.CultureInvariant);
        // "NullReferenceException: Object reference not set ..." or "System.IO.IOException: ...".
        static readonly Regex ExceptionTypeRe = new Regex("^([A-Za-z_][A-Za-z0-9_.`]*(?:Exception|Error))(?::|$)", RegexOptions.CultureInvariant);

        /// <summary>
        /// Frames of the logging machinery, not of the game: Debug.LogError,
        /// the logger, asserts and this SDK.
        /// </summary>
        static readonly string[] SkippedFrames =
        {
            "UnityEngine.Debug", "UnityEngine.Logger", "UnityEngine.DebugLogHandler", "UnityEngine.Assertions.",
            "UnityEngine.StackTraceUtility", "UntitledAnalyticsSdk.",
        };

        /// <summary>
        /// The first frame that is game code, from a Unity log stack or a .NET
        /// exception stack. Release builds often have no file and line: then
        /// only the function is set.
        /// </summary>
        public static StackFrameInfo FirstFrame(string stack)
        {
            var frame = new StackFrameInfo();
            if (string.IsNullOrEmpty(stack)) return frame;
            foreach (var raw in stack.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("at ", StringComparison.Ordinal)) line = line.Substring(3);
                if (line.Length == 0 || line.StartsWith("---", StringComparison.Ordinal)) continue;
                var skipped = false;
                foreach (var prefix in SkippedFrames)
                    if (line.StartsWith(prefix, StringComparison.Ordinal)) { skipped = true; break; }
                if (skipped) continue;
                var paren = line.IndexOf('(');
                var function = (paren > 0 ? line.Substring(0, paren) : line).Trim().Replace(':', '.');
                if (function.Length == 0) continue;
                frame.Function = function;
                var match = UnityAtRe.Match(line);
                if (!match.Success) match = DotNetInRe.Match(line);
                if (match.Success && match.Groups[1].Value != "<filename unknown>")
                {
                    frame.File = match.Groups[1].Value.Replace('\\', '/');
                    frame.Line = int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
                }
                return frame;
            }
            return frame;
        }

        /// <summary>The exception class from a Unity exception log line, short form (NullReferenceException), or "".</summary>
        public static string ExceptionType(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            var match = ExceptionTypeRe.Match(message);
            if (!match.Success) return "";
            var name = match.Groups[1].Value;
            var dot = name.LastIndexOf('.');
            return dot >= 0 ? name.Substring(dot + 1) : name;
        }

        /// <summary>
        /// Groups errors as the Godot SDK, PostHog and Sentry do: the error type
        /// and the place in the code (no line number, so an edit above it keeps
        /// the group), else the message without numbers and quoted values.
        /// </summary>
        public static string Fingerprint(string errorType, string message, string file, string function)
        {
            string basis;
            if (!string.IsNullOrEmpty(file) || !string.IsNullOrEmpty(function))
                basis = errorType + "|" + file + "|" + function;
            else
                basis = errorType + "|" + NumberRe.Replace(QuotedRe.Replace(message ?? "", "?"), "N");
            return Ids.Sha1Prefix(basis);
        }
    }
}
