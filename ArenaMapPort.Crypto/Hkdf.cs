#nullable disable
using System;

namespace ArenaMapPort.Crypto
{
    /// <summary>
    /// HKDF with HMAC-SHA-256 (RFC 5869): Extract turns the input keying material (the hashes of the player's Arena level files) into
    /// a pseudo-random key, Expand stretches a key into as many bytes as needed (the 72 bytes of the key-wrapping key, and the
    /// container subkeys derived from the map key). Written against <see cref="ICryptoProvider"/> so both providers produce the same
    /// bytes. Proved by BundleCryptoTests (RFC 5869 test cases 1-3 on both providers, equality with System.Security.Cryptography.HKDF).
    /// </summary>
    public static class Hkdf
    {
        const int HashLength = 32;

        /// <summary>HKDF-Extract: PRK = HMAC-SHA-256(key = salt, data = ikm). An empty salt means HashLen zero bytes (RFC 5869 2.2).</summary>
        public static byte[] Extract(ICryptoProvider provider, byte[] salt, byte[] ikm)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            if (ikm == null) throw new ArgumentNullException("ikm");
            if (salt == null || salt.Length == 0)
                salt = new byte[HashLength];
            return provider.HmacSha256(salt, ikm, 0, ikm.Length);
        }

        /// <summary>HKDF-Expand: T(i) = HMAC(prk, T(i-1) | info | i), the first <paramref name="length"/> bytes of T(1) | T(2) | ... (at most 255 * 32).</summary>
        public static byte[] Expand(ICryptoProvider provider, byte[] prk, byte[] info, int length)
        {
            if (provider == null) throw new ArgumentNullException("provider");
            if (prk == null) throw new ArgumentNullException("prk");
            if (info == null) info = new byte[0];
            if (length < 0 || length > 255 * HashLength)
                throw new ArgumentOutOfRangeException("length");
            byte[] output = new byte[length];
            byte[] previous = new byte[0];
            int produced = 0;
            for (int counter = 1; produced < length; counter++)
            {
                byte[] input = new byte[previous.Length + info.Length + 1];
                Buffer.BlockCopy(previous, 0, input, 0, previous.Length);
                Buffer.BlockCopy(info, 0, input, previous.Length, info.Length);
                input[input.Length - 1] = (byte)counter;
                previous = provider.HmacSha256(prk, input, 0, input.Length);
                int take = Math.Min(HashLength, length - produced);
                Buffer.BlockCopy(previous, 0, output, produced, take);
                produced += take;
            }
            return output;
        }
    }
}
