#nullable disable
using System;

namespace ArenaMapPort.Crypto
{
    /// <summary>
    /// Constant-time comparison of secrets (MAC values, key-wrap check values). <c>CryptographicOperations.FixedTimeEquals</c>
    /// does not exist in netstandard2.1, and EFT's Mono would not have it either, so the one loop lives here.
    /// Proved by BundleCryptoTests (key wrap and container MAC rejection).
    /// </summary>
    public static class ConstantTime
    {
        /// <summary>True when both arrays are non-null, equally long and equal. The time depends on the length, never on where a difference sits.</summary>
        public static bool AreEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null)
                return false;
            int diff = a.Length ^ b.Length;
            int n = a.Length < b.Length ? a.Length : b.Length;
            for (int i = 0; i < n; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    /// <summary>Lowercase hex encoding and strict hex decoding for the key file (shared by every project that links the Crypto folder).</summary>
    internal static class HexCodec
    {
        const string Digits = "0123456789abcdef";

        public static string ToHex(byte[] data)
        {
            if (data == null)
                return "";
            char[] chars = new char[data.Length * 2];
            for (int i = 0; i < data.Length; i++)
            {
                chars[2 * i] = Digits[data[i] >> 4];
                chars[2 * i + 1] = Digits[data[i] & 15];
            }
            return new string(chars);
        }

        /// <summary>Decodes exactly <paramref name="expectedBytes"/> bytes from 2 * expectedBytes hex digits (either case); false for anything else.</summary>
        public static bool TryParse(string text, int expectedBytes, out byte[] result)
        {
            result = null;
            if (text == null || text.Length != expectedBytes * 2)
                return false;
            byte[] r = new byte[expectedBytes];
            for (int i = 0; i < expectedBytes; i++)
            {
                int hi = Nibble(text[2 * i]);
                int lo = Nibble(text[2 * i + 1]);
                if (hi < 0 || lo < 0)
                    return false;
                r[i] = (byte)((hi << 4) | lo);
            }
            result = r;
            return true;
        }

        public static byte[] Parse(string text)
        {
            if (text == null || text.Length % 2 != 0)
                throw new FormatException("hex text must have an even length");
            byte[] r;
            if (!TryParse(text, text.Length / 2, out r))
                throw new FormatException("hex text contains a character that is not a hex digit");
            return r;
        }

        static int Nibble(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
