using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ManalensSdk
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
            "UnityEngine.StackTraceUtility", "ManalensSdk.",
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
                // Release players have no file: "<filename unknown>", or "<module ID>:0", an ID that changes with each build.
                if (match.Success && !match.Groups[1].Value.StartsWith("<", StringComparison.Ordinal))
                {
                    frame.File = match.Groups[1].Value.Replace('\\', '/');
                    frame.Line = int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
                }
                return frame;
            }
            return frame;
        }

        /// <summary>
        /// True when the first frame after the logging and .NET library frames
        /// is Unity Editor code: an error of the editor itself, which Play mode
        /// also logs. A player has no UnityEditor frames.
        /// </summary>
        public static bool IsEditorStack(string stack)
        {
            if (string.IsNullOrEmpty(stack)) return false;
            foreach (var raw in stack.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("at ", StringComparison.Ordinal)) line = line.Substring(3);
                if (line.Length == 0 || line.StartsWith("---", StringComparison.Ordinal)) continue;
                if (line.StartsWith("System.", StringComparison.Ordinal) || line.StartsWith("Mono.", StringComparison.Ordinal)) continue;
                var skipped = false;
                foreach (var prefix in SkippedFrames)
                    if (line.StartsWith(prefix, StringComparison.Ordinal)) { skipped = true; break; }
                if (skipped) continue;
                return line.StartsWith("UnityEditor.", StringComparison.Ordinal);
            }
            return false;
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

        /// <summary>
        /// Removes personal data from a captured log message, with the rules of
        /// the Unreal plugin: the home folders and /Users/&lt;name&gt;,
        /// /home/&lt;name&gt;, X:\Users\&lt;name&gt; become ~; URLs lose their user,
        /// query and fragment; emails become [email]; the values after key=,
        /// token=, secret=, password=, authorization: and bearer, and runs of 24
        /// or more letters, digits, _ and - become [redacted].
        /// </summary>
        public static string CleanLogMessage(string message, IEnumerable<string> homeDirs)
        {
            var text = message ?? "";
            if (homeDirs != null)
                foreach (var dir in homeDirs) text = CleanDir(text, dir);
            text = CleanHomePatterns(text);
            text = CleanUrls(text);
            text = MaskEmails(text);
            text = MaskKeyValues(text);
            return MaskLongTokens(text);
        }

        /// <summary>
        /// Cleans one crash stack frame (crash stacks): the home folders become ~,
        /// URLs lose their user, query and fragment, and emails become [email].
        /// Long names are kept: a frame such as an IL2CPP method name is code,
        /// not a token, and the fingerprint needs it.
        /// </summary>
        public static string CleanStackFrame(string frame, IEnumerable<string> homeDirs)
        {
            var text = frame ?? "";
            if (homeDirs != null)
                foreach (var dir in homeDirs) text = CleanDir(text, dir);
            text = CleanHomePatterns(text);
            text = CleanUrls(text);
            return MaskEmails(text);
        }

        /// <summary>The player's home folders, as the OS names them.</summary>
        public static List<string> HomeDirs()
        {
            var dirs = new List<string>();
            try
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(profile)) dirs.Add(profile);
                foreach (var name in new[] { "HOME", "USERPROFILE" })
                {
                    var value = Environment.GetEnvironmentVariable(name);
                    if (!string.IsNullOrEmpty(value) && !dirs.Contains(value)) dirs.Add(value);
                }
            }
            catch (Exception)
            {
                // Some platforms (WebGL, consoles) have no home folder: the patterns still apply.
            }
            return dirs;
        }

        static bool IsAsciiAlnum(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
        static bool IsSeparator(char c) => c == '/' || c == '\\';
        static bool IsTokenChar(char c) => IsAsciiAlnum(c) || c == '_' || c == '-';
        static bool IsEnd(char c) => char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == '<' || c == '>' || c == '(' || c == ')';
        static bool IsEmailLocal(char c) => IsAsciiAlnum(c) || c == '.' || c == '_' || c == '%' || c == '+' || c == '-';
        static bool IsEmailDomain(char c) => IsAsciiAlnum(c) || c == '.' || c == '-';

        /// <summary>Each occurrence of a directory (and so the path under it) becomes ~.</summary>
        static string CleanDir(string text, string dir)
        {
            dir = (dir ?? "").Replace('\\', '/');
            while (dir.Length > 0 && IsSeparator(dir[dir.Length - 1])) dir = dir.Substring(0, dir.Length - 1);
            if (dir.Length < 3) return text;
            foreach (var form in new[] { dir, dir.Replace('/', '\\') })
            {
                var from = 0;
                int at;
                while ((at = text.IndexOf(form, from, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var end = at + form.Length;
                    if (end < text.Length && !IsSeparator(text[end]) && !IsEnd(text[end]))
                    {
                        from = end; // A longer name, for example /Users/annabel for /Users/anna.
                        continue;
                    }
                    text = text.Substring(0, at) + "~" + text.Substring(end);
                    from = at + 1;
                }
            }
            return text;
        }

        /// <summary>/Users/&lt;name&gt;, /home/&lt;name&gt;, and X:\Users\&lt;name&gt; become ~.</summary>
        static string CleanHomePatterns(string text)
        {
            var sb = new StringBuilder(text.Length);
            var index = 0;
            while (index < text.Length)
            {
                var nameAt = -1;
                var rest = text.Substring(index, Math.Min(9, text.Length - index));
                var boundary = index == 0 || !(IsAsciiAlnum(text[index - 1]) || text[index - 1] == '_');
                if (rest.StartsWith("/Users/", StringComparison.OrdinalIgnoreCase)) nameAt = index + 7;
                else if (rest.StartsWith("/home/", StringComparison.OrdinalIgnoreCase)) nameAt = index + 6;
                else if (boundary && rest.Length == 9 && char.IsLetter(rest[0]) && rest[1] == ':' && IsSeparator(rest[2])
                    && string.Equals(rest.Substring(3, 5), "Users", StringComparison.OrdinalIgnoreCase) && IsSeparator(rest[8]))
                    nameAt = index + 9;
                if (nameAt >= 0)
                {
                    var end = nameAt;
                    while (end < text.Length && !IsSeparator(text[end]) && !IsEnd(text[end])) end++;
                    if (end > nameAt)
                    {
                        sb.Append('~');
                        index = end;
                        continue;
                    }
                }
                sb.Append(text[index]);
                index++;
            }
            return sb.ToString();
        }

        /// <summary>scheme://user:pass@host/path?query#fragment becomes scheme://host/path.</summary>
        static string CleanUrls(string text)
        {
            var from = 0;
            int at;
            while ((at = text.IndexOf("://", from, StringComparison.Ordinal)) >= 0)
            {
                var end = at + 3;
                while (end < text.Length && !IsEnd(text[end])) end++;
                var rest = text.Substring(at + 3, end - at - 3);
                var cut = rest.IndexOfAny(new[] { '?', '#' });
                if (cut >= 0) rest = rest.Substring(0, cut);
                var slash = rest.IndexOf('/');
                var authority = slash < 0 ? rest : rest.Substring(0, slash);
                var path = slash < 0 ? "" : rest.Substring(slash);
                var atSign = authority.LastIndexOf('@');
                if (atSign >= 0) authority = authority.Substring(atSign + 1);
                var clean = authority + path;
                text = text.Substring(0, at + 3) + clean + text.Substring(end);
                from = at + 3 + clean.Length;
            }
            return text;
        }

        static string MaskEmails(string text)
        {
            for (var at = 0; at < text.Length; at++)
            {
                if (text[at] != '@') continue;
                var start = at;
                while (start > 0 && IsEmailLocal(text[start - 1])) start--;
                var end = at + 1;
                while (end < text.Length && IsEmailDomain(text[end])) end++;
                while (end > at + 1 && (text[end - 1] == '.' || text[end - 1] == '-')) end--; // A full stop after the address.
                if (start < at && text.Substring(at + 1, end - at - 1).Contains("."))
                {
                    text = text.Substring(0, start) + "[email]" + text.Substring(end);
                    at = start + 6;
                }
            }
            return text;
        }

        /// <summary>
        /// The value after key, token, secret, password or authorization with = or :
        /// (also the JSON form "token": "...") and after bearer. A quoted value is
        /// redacted up to its closing quote.
        /// </summary>
        static string MaskKeyValues(string text)
        {
            const string redacted = "[redacted]";
            // bearer first, so "token: Bearer abc" loses both words.
            foreach (var name in new[] { "bearer", "authorization", "key", "token", "secret", "password" })
            {
                var from = 0;
                int at;
                while ((at = text.IndexOf(name, from, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var start = at + name.Length;
                    if (name == "bearer")
                    {
                        if (start >= text.Length || text[start] != ' ')
                        {
                            from = at + 1;
                            continue;
                        }
                    }
                    else
                    {
                        if (start < text.Length && (text[start] == '"' || text[start] == '\'')) start++; // the closing quote of a JSON key
                        while (start < text.Length && text[start] == ' ') start++;
                        if (start >= text.Length || (text[start] != ':' && text[start] != '='))
                        {
                            from = at + 1;
                            continue;
                        }
                        start++;
                    }
                    while (start < text.Length && text[start] == ' ') start++;
                    int end;
                    if (start < text.Length && (text[start] == '"' || text[start] == '\''))
                    {
                        // A quoted value: up to the closing quote (or the end of the text).
                        var quote = text[start];
                        start++;
                        end = text.IndexOf(quote, start);
                        if (end < 0) end = text.Length;
                    }
                    else if (name == "authorization")
                    {
                        // Authorization: <scheme> <credentials>, to the end of the line.
                        end = start;
                        while (end < text.Length && text[end] != '\n' && text[end] != '\r' && text[end] != '"' && text[end] != '\'') end++;
                    }
                    else
                    {
                        end = start;
                        while (end < text.Length && !IsEnd(text[end]) && text[end] != '&' && text[end] != ',' && text[end] != ';' && text[end] != ']' && text[end] != '}') end++;
                    }
                    if (end > start && text.Substring(start, end - start) != redacted)
                    {
                        text = text.Substring(0, start) + redacted + text.Substring(end);
                        end = start + redacted.Length;
                    }
                    from = Math.Max(end, at + 1);
                }
            }
            return text;
        }

        static bool IsBase64Char(char c) => IsTokenChar(c) || c == '+' || c == '/' || c == '=';

        /// <summary>
        /// Keys, tokens and IDs: a JWT (eyJ... with two dots), a base64 run of 24 or
        /// more characters with + or a trailing =, and runs of 24 or more characters
        /// from A-Z, a-z, 0-9, _ and -. File paths (with / but no + or =) stay.
        /// </summary>
        static string MaskLongTokens(string text)
        {
            var sb = new StringBuilder(text.Length);
            var index = 0;
            while (index < text.Length)
            {
                var end = index;
                while (end < text.Length && IsTokenChar(text[end])) end++;
                var redactTo = -1;
                if (string.CompareOrdinal(text, index, "eyJ", 0, 3) == 0)
                {
                    var jwtEnd = index;
                    var dots = 0;
                    while (jwtEnd < text.Length && (IsTokenChar(text[jwtEnd]) || text[jwtEnd] == '.'))
                    {
                        if (text[jwtEnd] == '.') dots++;
                        jwtEnd++;
                    }
                    while (jwtEnd > index && text[jwtEnd - 1] == '.')
                    {
                        jwtEnd--; // a full stop after the token
                        dots--;
                    }
                    if (dots >= 2) redactTo = jwtEnd;
                }
                if (redactTo < 0)
                {
                    var b64End = index;
                    while (b64End < text.Length && IsBase64Char(text[b64End])) b64End++;
                    var length = b64End - index;
                    if (length >= 24 && (text.IndexOf('+', index, length) >= 0 || text[b64End - 1] == '=')) redactTo = b64End;
                }
                if (redactTo < 0 && end - index >= 24) redactTo = end;
                if (redactTo >= 0)
                {
                    sb.Append("[redacted]");
                    index = redactTo;
                }
                else if (end > index)
                {
                    sb.Append(text, index, end - index);
                    index = end;
                }
                else
                {
                    sb.Append(text[index]);
                    index++;
                }
            }
            return sb.ToString();
        }
    }
}
