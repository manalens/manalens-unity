namespace UntitledAnalyticsSdk
{
    /// <summary>
    /// Key rules, the same as packages/shared/src/keys.ts in the collector. A
    /// game build may only hold the publishable key.
    /// </summary>
    public static class Keys
    {
        public const string PublishablePrefix = "ua_pk_";
        public const string SecretPrefix = "ua_sk_";
        public const string ApiPrefix = "ua_api_";
        public const string LegacyApiPrefix = "ua_mcp_";
        const int BodyLength = 43;
        const int ChecksumLength = 8;
        const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        static uint[] _crcTable;

        /// <summary>Why the value cannot be used as a publishable key, or null when it may be one.</summary>
        public static string PublishableKeyRefusal(string key)
        {
            if (string.IsNullOrEmpty(key)) return "PublishableKey is required";
            if (key.StartsWith(SecretPrefix, System.StringComparison.Ordinal))
                return "this is a secret key (ua_sk_). Never put a secret key in a game build; use the publishable key (ua_pk_...)";
            if (key.StartsWith(ApiPrefix, System.StringComparison.Ordinal) || key.StartsWith(LegacyApiPrefix, System.StringComparison.Ordinal))
                return "this is an API key. Never put an API key in a game build; use the publishable key (ua_pk_...)";
            if (key.StartsWith(PublishablePrefix, System.StringComparison.Ordinal) && !IsWellFormedNewKey(key, PublishablePrefix))
                return "malformed publishable key (check that you copied all of it)";
            return null;
        }

        /// <summary>A new key: the prefix, 43 base62 characters and an 8-character CRC32 checksum.</summary>
        public static bool IsWellFormedNewKey(string key, string prefix)
        {
            if (key.Length != prefix.Length + BodyLength + ChecksumLength) return false;
            for (var i = prefix.Length; i < key.Length; i++)
                if (Base62.IndexOf(key[i]) < 0) return false;
            var split = key.Length - ChecksumLength;
            return Checksum(key.Substring(0, split)) == key.Substring(split);
        }

        /// <summary>CRC32 (IEEE) of the ASCII text, in base62, zero-padded to 8 characters.</summary>
        public static string Checksum(string prefixAndBody)
        {
            var value = Crc32(prefixAndBody);
            var chars = new char[ChecksumLength];
            for (var i = ChecksumLength - 1; i >= 0; i--)
            {
                chars[i] = Base62[(int)(value % 62)];
                value /= 62;
            }
            return new string(chars);
        }

        public static uint Crc32(string text)
        {
            if (_crcTable == null)
            {
                var table = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    var c = n;
                    for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
                    table[n] = c;
                }
                _crcTable = table;
            }
            var crc = 0xffffffff;
            foreach (var ch in text) crc = _crcTable[(crc ^ ch) & 0xff] ^ (crc >> 8);
            return crc ^ 0xffffffff;
        }
    }
}
