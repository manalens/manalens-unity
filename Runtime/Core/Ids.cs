using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UntitledAnalyticsSdk
{
    public static class Ids
    {
        static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        /// <summary>A random UUID version 4 (RFC 9562), lower case.</summary>
        public static string NewUuid()
        {
            var bytes = new byte[16];
            lock (Rng) Rng.GetBytes(bytes);
            bytes[6] = (byte)((bytes[6] & 0x0f) | 0x40);
            bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
            var hex = Hex(bytes);
            return hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20, 12);
        }

        /// <summary>ISO 8601 UTC with milliseconds, for example 2026-11-16T08:30:00.123Z.</summary>
        public static string Iso(double unixSeconds)
        {
            var ms = (long)Math.Floor(unixSeconds * 1000.0);
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        /// <summary>The first 16 hex characters of the SHA-1 of the UTF-8 text (as Godot sha1_text().left(16)).</summary>
        public static string Sha1Prefix(string text)
        {
            using (var sha = SHA1.Create())
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Substring(0, 16);
        }

        static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
