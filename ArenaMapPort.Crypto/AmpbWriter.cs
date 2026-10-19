#nullable disable
using System;
using System.IO;

namespace ArenaMapPort.Crypto
{
    public static partial class Ampb
    {
        /// <summary>
        /// Encrypts <paramref name="plain"/> (from its current position to its end, which must be seekable: it is read twice) into an AMPB
        /// container written to <paramref name="dest"/> starting at its current position (seekable too: the header and the chunk table sit
        /// in front of a payload whose hashes are only known afterwards, so the header is written last; a fresh, empty destination is expected).
        /// Pass 1 computes the nonce (HMAC-SHA-256 of the whole plaintext under K_nonce), pass 2 encrypts chunk by chunk (AES-256-CTR) and
        /// hashes each ciphertext chunk. The output depends on nothing but the plaintext, the map key, K_map and the chunk size: the same inputs give
        /// the same bytes, on either provider. The source must not change while this runs. Memory use is one chunk. Returns the written header
        /// (authenticated: it was produced with the key).
        /// </summary>
        public static AmpbHeader Encrypt(Stream plain, Stream dest, string mapKey, byte[] kMap, ICryptoProvider provider = null, int chunkSize = DefaultChunkSize)
        {
            if (plain == null) throw new ArgumentNullException("plain");
            if (dest == null) throw new ArgumentNullException("dest");
            if (!plain.CanRead || !plain.CanSeek) throw new ArgumentException("the plaintext stream must be readable and seekable", "plain");
            if (!dest.CanWrite || !dest.CanSeek) throw new ArgumentException("the destination stream must be writable and seekable", "dest");
            if (!IsValidMapKey(mapKey)) throw new ArgumentException("the map key must be 1.." + MaxMapKeyLength + " characters of [a-z0-9_]", "mapKey");
            RequireKey(kMap);
            if (chunkSize < MinChunkSize || chunkSize > MaxChunkSize || (chunkSize & 15) != 0)
                throw new ArgumentOutOfRangeException("chunkSize", "the chunk size must be a multiple of 16 between 4 KiB and 16 MiB");
            if (provider == null)
                provider = CryptoProviders.Select(null);

            long plainStart = plain.Position;
            long plainLength = plain.Length - plainStart;
            if (plainLength <= 0)
                throw new ArgumentException("an empty plaintext is refused", "plain");
            long chunkCountValue = plainLength / chunkSize + (plainLength % chunkSize != 0 ? 1 : 0);
            if (chunkCountValue > MaxChunkCount)
                throw new ArgumentException("the plaintext needs more than " + MaxChunkCount + " chunks; use a larger chunk size", "chunkSize");
            int chunkCount = (int)chunkCountValue;
            int keyLength = mapKey.Length;
            long tableOffset = (FixedHeaderSize + keyLength + 15) & ~15L;
            long payloadOffset = tableOffset + 32L * chunkCount + 32;
            long destStart = dest.Position;

            AmpbKeys keys = AmpbKeys.Derive(kMap, provider);
            try
            {
                // pass 1: the nonce
                byte[] buffer = new byte[(int)Math.Min((long)chunkSize, plainLength)];
                byte[] nonce = new byte[8];
                using (IHashStream mac = provider.NewHmacSha256(keys.Nonce))
                {
                    long left = plainLength;
                    while (left > 0)
                    {
                        int n = (int)Math.Min((long)buffer.Length, left);
                        ReadSource(plain, buffer, n);
                        mac.Update(buffer, 0, n);
                        left -= n;
                    }
                    Buffer.BlockCopy(mac.Finish(), 0, nonce, 0, 8);
                }

                // pass 2: encrypt and hash the ciphertext, payload first
                plain.Position = plainStart;
                dest.Position = destStart + payloadOffset;
                byte[] table = new byte[32 * chunkCount];
                using (IAesKey aes = provider.NewAes(keys.Enc))
                {
                    for (int i = 0; i < chunkCount; i++)
                    {
                        int n = (int)Math.Min((long)chunkSize, plainLength - (long)i * chunkSize);
                        ReadSource(plain, buffer, n);
                        AesCtr.Transform(aes, nonce, (ulong)((long)i * chunkSize / 16), buffer, 0, buffer, 0, n);
                        byte[] hash = provider.Sha256(buffer, 0, n);
                        Buffer.BlockCopy(hash, 0, table, 32 * i, 32);
                        dest.Write(buffer, 0, n);
                    }
                }

                // header, key, padding, table, MAC
                byte[] region = new byte[tableOffset + 32L * chunkCount];
                region[0] = 0x41; region[1] = 0x4D; region[2] = 0x50; region[3] = 0x42;
                WriteU16(region, 4, FormatVersion);
                WriteU16(region, 6, 0);
                WriteU32(region, 8, chunkSize);
                WriteU32(region, 12, chunkCount);
                WriteU64(region, 16, plainLength);
                WriteU64(region, 24, payloadOffset);
                Buffer.BlockCopy(nonce, 0, region, 32, 8);
                WriteU16(region, 40, keyLength);
                for (int i = 0; i < keyLength; i++)
                    region[FixedHeaderSize + i] = (byte)mapKey[i];
                Buffer.BlockCopy(table, 0, region, (int)tableOffset, table.Length);
                byte[] macValue = provider.HmacSha256(keys.Mac, region, 0, region.Length);
                dest.Position = destStart;
                dest.Write(region, 0, region.Length);
                dest.Write(macValue, 0, macValue.Length);
                dest.SetLength(destStart + payloadOffset + plainLength);
                dest.Position = destStart + payloadOffset + plainLength;
                dest.Flush();

                AmpbHeader h = new AmpbHeader(FormatVersion, 0, chunkSize, chunkCount, plainLength, tableOffset, payloadOffset,
                    payloadOffset + plainLength, nonce, mapKey);
                h.ChunkHashes = table;
                h.Mac = macValue;
                h.Authenticated = true;
                return h;
            }
            finally
            {
                keys.Clear();
            }
        }

        static void ReadSource(Stream plain, byte[] buffer, int count)
        {
            int got = ReadUpTo(plain, buffer, count);
            if (got != count)
                throw new IOException("the plaintext ended " + (count - got) + " bytes early (the source changed while it was being encrypted)");
        }
    }
}
