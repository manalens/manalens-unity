using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UntitledAnalyticsSdk
{
    /// <summary>
    /// The SDK state (consent, identity, open session) as "key=value" lines
    /// between a header line and an end line, so a cut-off file is detected.
    /// Values escape backslash, newline and carriage return.
    /// </summary>
    public sealed class StateFile
    {
        readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        const string Header = "# untitled_analytics state v1";
        const string Footer = "# end";

        /// <summary>
        /// Parses the text that Serialize() wrote. Returns false for anything
        /// else: an empty or cut-off file (no end line), another format, or a
        /// line without "=". The caller must then treat the state as unknown.
        /// </summary>
        public static bool TryParse(string text, out StateFile state)
        {
            state = new StateFile();
            if (string.IsNullOrEmpty(text)) return false;
            var lines = text.Split('\n');
            // Serialize() ends with a newline, so the last item is empty.
            if (lines.Length < 3 || lines[0] != Header || lines[lines.Length - 1] != "" || lines[lines.Length - 2] != Footer) return false;
            for (var i = 1; i < lines.Length - 2; i++)
            {
                var line = lines[i];
                var at = line.IndexOf('=');
                if (at <= 0) return false;
                state._values[line.Substring(0, at)] = Unescape(line.Substring(at + 1));
            }
            return true;
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            foreach (var pair in _values) sb.Append(pair.Key).Append('=').Append(Escape(pair.Value)).Append('\n');
            sb.Append(Footer).Append('\n');
            return sb.ToString();
        }

        public bool Has(string key) => _values.ContainsKey(key);
        public string Get(string key, string fallback = "") => _values.TryGetValue(key, out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value ?? "";
        public void Remove(string key) => _values.Remove(key);

        public double GetDouble(string key, double fallback) =>
            double.TryParse(Get(key, null), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
        public void SetDouble(string key, double value) => Set(key, value.ToString("R", CultureInfo.InvariantCulture));
        public bool GetBool(string key, bool fallback) => Has(key) ? Get(key) == "true" : fallback;
        public void SetBool(string key, bool value) => Set(key, value ? "true" : "false");

        /// <summary>Removes every key that starts with the prefix, for example "session.".</summary>
        public void RemovePrefix(string prefix)
        {
            var keys = new List<string>();
            foreach (var key in _values.Keys) if (key.StartsWith(prefix, System.StringComparison.Ordinal)) keys.Add(key);
            foreach (var key in keys) _values.Remove(key);
        }

        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r");

        static string Unescape(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '\\' && i + 1 < value.Length)
                {
                    var next = value[++i];
                    sb.Append(next == 'n' ? '\n' : next == 'r' ? '\r' : next);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
