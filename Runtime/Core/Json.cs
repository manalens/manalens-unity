using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ManalensSdk
{
    /// <summary>
    /// A small JSON writer (no dependencies; JsonUtility cannot write
    /// dictionaries). Numbers use the invariant culture, so a German or French
    /// system locale never writes "1,5".
    /// </summary>
    public static class Json
    {
        public static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        // Control characters, and the line separators that break some JS parsers.
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>Writes a scalar: null, bool, string or a finite number. Callers clean values first (see Scalars).</summary>
        public static void WriteScalar(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case string s: WriteString(sb, s); break;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); break;
                case IFormattable n: sb.Append(n.ToString(null, CultureInfo.InvariantCulture)); break;
                default: WriteString(sb, value.ToString()); break;
            }
        }

        public static void WriteObject(StringBuilder sb, IEnumerable<KeyValuePair<string, object>> values)
        {
            sb.Append('{');
            var first = true;
            foreach (var pair in values)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, pair.Key);
                sb.Append(':');
                WriteScalar(sb, pair.Value);
            }
            sb.Append('}');
        }
    }
}
