#nullable disable
using System;

namespace ArenaMapPort.Crypto
{
    /// <summary>
    /// AES-256 in counter mode on top of an ECB block encryptor. The counter block for block index i is
    /// <c>nonce[8] || uint64_BigEndian(i)</c>; keystream block i = AES(key, counter block i); data = data XOR keystream (the same
    /// call encrypts and decrypts). Block i is addressed directly, so a reader decrypts any 16-aligned offset of a bundle without
    /// touching earlier data (random access for AssetBundle.LoadFromStream). The work is done in 64 KiB slices through two
    /// per-thread scratch buffers (counter blocks, keystream), so a 1 MiB chunk needs no allocation and the loop stays in cache.
    /// The XOR and the counter fill are the only unsafe code of the feature (ulong pointers; no Span dependency on Mono; the host
    /// is little-endian x86-64, a big-endian host takes the byte-wise path). Proved by BundleCryptoTests (NIST SP 800-38A F.5.5,
    /// equality with an independent counter-block construction at odd offsets and counts).
    /// </summary>
    public static class AesCtr
    {
        const int SliceBytes = 64 * 1024;

        [ThreadStatic]
        static byte[] _counters;
        [ThreadStatic]
        static byte[] _keystream;

        /// <summary>
        /// Transforms <paramref name="count"/> bytes of <paramref name="src"/> into <paramref name="dst"/>, where the first byte sits at
        /// counter block <paramref name="firstBlock"/> (offset firstBlock * 16 of the stream). The two ranges may be the same range
        /// (in place) but must not partially overlap. A count that is not a multiple of 16 is fine for the last call of a stream.
        /// </summary>
        public static unsafe void Transform(IAesKey key, byte[] nonce8, ulong firstBlock, byte[] src, int srcOffset, byte[] dst, int dstOffset, int count)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (nonce8 == null || nonce8.Length != 8) throw new ArgumentException("the nonce is 8 bytes", "nonce8");
            ArgCheck.Range(src, srcOffset, count, "src");
            ArgCheck.Range(dst, dstOffset, count, "dst");
            if (count == 0)
                return;

            byte[] counters = _counters ?? (_counters = new byte[SliceBytes]);
            byte[] keystream = _keystream ?? (_keystream = new byte[SliceBytes]);

            int done = 0;
            ulong block = firstBlock;
            while (done < count)
            {
                int slice = Math.Min(SliceBytes, count - done);
                int blocks = (slice + 15) >> 4;
                FillCounters(counters, nonce8, block, blocks);
                key.EncryptEcb(counters, 0, keystream, 0, blocks << 4);
                fixed (byte* s0 = src, d0 = dst, k0 = keystream)
                    Xor(s0 + srcOffset + done, d0 + dstOffset + done, k0, slice);
                done += slice;
                block += (ulong)blocks;
            }
        }

        static unsafe void FillCounters(byte[] counters, byte[] nonce8, ulong firstBlock, int blocks)
        {
            if (BitConverter.IsLittleEndian)
            {
                fixed (byte* c0 = counters, n0 = nonce8)
                {
                    ulong nonce = *(ulong*)n0;
                    ulong* words = (ulong*)c0;
                    ulong index = firstBlock;
                    for (int i = 0; i < blocks; i++, index++)
                    {
                        words[2 * i] = nonce;
                        words[2 * i + 1] = ByteSwap(index);
                    }
                }
                return;
            }
            ulong value = firstBlock;
            for (int i = 0; i < blocks; i++, value++)
            {
                int o = i << 4;
                Buffer.BlockCopy(nonce8, 0, counters, o, 8);
                for (int b = 0; b < 8; b++)
                    counters[o + 8 + b] = (byte)(value >> (56 - 8 * b));
            }
        }

        static ulong ByteSwap(ulong v)
        {
            v = ((v & 0x00FF00FF00FF00FFUL) << 8) | ((v >> 8) & 0x00FF00FF00FF00FFUL);
            v = ((v & 0x0000FFFF0000FFFFUL) << 16) | ((v >> 16) & 0x0000FFFF0000FFFFUL);
            return (v << 32) | (v >> 32);
        }

        static unsafe void Xor(byte* src, byte* dst, byte* keystream, int count)
        {
            int words = count >> 3;
            ulong* s = (ulong*)src;
            ulong* d = (ulong*)dst;
            ulong* k = (ulong*)keystream;
            for (int i = 0; i < words; i++)
                d[i] = s[i] ^ k[i];
            for (int i = words << 3; i < count; i++)
                dst[i] = (byte)(src[i] ^ keystream[i]);
        }
    }
}
