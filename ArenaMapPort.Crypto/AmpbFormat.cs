#nullable disable
using System;
using System.IO;
using System.Text;

namespace ArenaMapPort.Crypto
{
    /// <summary>What was wrong with an AMPB container (or with a chunk of one).</summary>
    public enum AmpbError
    {
        /// <summary>The file does not start with "AMPB" (a plaintext UnityFS bundle, for example).</summary>
        BadMagic,
        /// <summary>A version or flags value this reader does not understand.</summary>
        BadVersion,
        /// <summary>A structural defect: chunk size, counts, lengths, key length or map key outside the rules, or bytes after the payload.</summary>
        BadHeader,
        /// <summary>The file is shorter than its header says (truncated download or copy).</summary>
        Truncated,
        /// <summary>The header MAC does not verify: wrong content key, a different bundle set, or a modified header or chunk table.</summary>
        HeaderAuth,
        /// <summary>The container belongs to another map than the one being served.</summary>
        WrongMap,
        /// <summary>The hash of a ciphertext chunk differs from the table: the file is damaged. <see cref="AmpbException.Chunk"/> names it.</summary>
        ChunkIntegrity,
    }

    /// <summary>
    /// The one exception type every container defect surfaces as (an <see cref="IOException"/>, so a stream consumer that already handles
    /// IO errors keeps working). A flipped byte anywhere in a container produces this type or a successful MAC check, never a
    /// different exception. Proved by BundleCryptoTests (all of the first 256 bytes flipped one at a time, table, MAC, payload).
    /// </summary>
    public sealed class AmpbException : IOException
    {
        public AmpbError Kind { get; private set; }
        /// <summary>The chunk index for <see cref="AmpbError.ChunkIntegrity"/>, otherwise -1.</summary>
        public int Chunk { get; private set; }

        public AmpbException(AmpbError kind, string message) : this(kind, -1, message) { }

        public AmpbException(AmpbError kind, int chunk, string message) : base(message)
        {
            Kind = kind;
            Chunk = chunk;
        }
    }

    /// <summary>
    /// The decoded fixed header and chunk table of an AMPB container (layout in <see cref="Ampb"/>). Immutable; <see cref="Authenticated"/>
    /// is true only for a header whose MAC was verified with the content key (<see cref="Ampb.ReadHeader"/>); <see cref="Ampb.Inspect(Stream)"/>
    /// returns unauthenticated headers (structure only).
    /// </summary>
    public sealed class AmpbHeader
    {
        readonly byte[] _nonce;
        internal byte[] ChunkHashes;
        internal byte[] MacInput;
        internal byte[] Mac;

        internal AmpbHeader(int version, int flags, int chunkSize, int chunkCount, long plaintextLength, long tableOffset, long payloadOffset,
            long fileLength, byte[] nonce, string mapKey)
        {
            Version = version;
            Flags = flags;
            ChunkSize = chunkSize;
            ChunkCount = chunkCount;
            PlaintextLength = plaintextLength;
            TableOffset = tableOffset;
            PayloadOffset = payloadOffset;
            FileLength = fileLength;
            _nonce = nonce;
            MapKey = mapKey;
        }

        public int Version { get; private set; }
        public int Flags { get; private set; }
        public int ChunkSize { get; private set; }
        public int ChunkCount { get; private set; }
        /// <summary>Length of the decrypted bundle (the Length of the decrypting stream).</summary>
        public long PlaintextLength { get; private set; }
        public long TableOffset { get; private set; }
        /// <summary>Absolute offset of the ciphertext in the container file.</summary>
        public long PayloadOffset { get; private set; }
        /// <summary>Length of the whole container file (PayloadOffset + PlaintextLength).</summary>
        public long FileLength { get; private set; }
        public string MapKey { get; private set; }
        public bool Authenticated { get; internal set; }

        /// <summary>The 8-byte CTR nonce (a copy).</summary>
        public byte[] Nonce
        {
            get { return (byte[])_nonce.Clone(); }
        }

        internal byte[] NonceBytes
        {
            get { return _nonce; }
        }

        /// <summary>The table entry for ciphertext chunk <paramref name="index"/> (SHA-256, a copy).</summary>
        public byte[] GetChunkHash(int index)
        {
            if (index < 0 || index >= ChunkCount || ChunkHashes == null)
                throw new ArgumentOutOfRangeException("index");
            byte[] r = new byte[32];
            Buffer.BlockCopy(ChunkHashes, index * 32, r, 0, 32);
            return r;
        }

        /// <summary>Length of plaintext (= ciphertext) chunk <paramref name="index"/>: the chunk size, except for the shorter last chunk.</summary>
        public int GetChunkLength(int index)
        {
            if (index < 0 || index >= ChunkCount)
                throw new ArgumentOutOfRangeException("index");
            long remaining = PlaintextLength - (long)index * ChunkSize;
            return (int)Math.Min((long)ChunkSize, remaining);
        }
    }

    /// <summary>Result of <see cref="Ampb.Inspect(Stream)"/>: what a file is, without any key.</summary>
    public sealed class AmpbInfo
    {
        internal AmpbInfo(bool isAmpb, AmpbHeader header, AmpbException error)
        {
            IsAmpb = isAmpb;
            Header = header;
            Error = error;
        }

        /// <summary>True when the file starts with the AMPB magic.</summary>
        public bool IsAmpb { get; private set; }
        /// <summary>The decoded header, or null when the structure is invalid. Not authenticated.</summary>
        public AmpbHeader Header { get; private set; }
        /// <summary>The structural defect, or null when the container is structurally valid.</summary>
        public AmpbException Error { get; private set; }
        public bool IsValid { get { return IsAmpb && Error == null; } }
    }

    /// <summary>
    /// The AMPB v1 container: an encrypted map bundle that can be decrypted chunk by chunk. All integers are little-endian.
    /// <code>
    /// offset  size          field
    ///      0     4          magic "AMPB"
    ///      4     2          version = 1
    ///      6     2          flags = 0 (reserved; non-zero is refused)
    ///      8     4          chunk_size (multiple of 16, 4 KiB..16 MiB; the build uses 1 MiB)
    ///     12     4          chunk_count = ceil(plaintext_length / chunk_size)
    ///     16     8          plaintext_length (&gt; 0)
    ///     24     8          payload_offset = table_offset + 32 * chunk_count + 32
    ///     32     8          nonce
    ///     40     2          key_len n (1..64)
    ///     42     n          map_key, ASCII [a-z0-9_]
    ///     ..   pad          zero bytes up to table_offset = (42 + n + 15) &amp; ~15
    ///     table_offset  32 * chunk_count   SHA-256 of every CIPHERTEXT chunk (the last one is shorter)
    ///     ..    32          HMAC-SHA-256(K_mac, every byte above: header, key, padding, table)
    ///     payload_offset  plaintext_length   AES-256-CTR ciphertext, no expansion
    /// </code>
    /// The file length must equal payload_offset + plaintext_length. Subkeys come from the content key K_map by HKDF-Expand (K_map is
    /// uniform, so Extract is skipped, RFC 5869 3.3): K_enc, K_mac, K_nonce = HMAC(K_map, "ArenaMapPort bundle enc|mac|nonce v1" 01).
    /// Counter block i = nonce[8] || uint64_BE(i), see <see cref="AesCtr"/>. The nonce is the first 8 bytes of
    /// HMAC-SHA-256(K_nonce, plaintext), so an unchanged bundle encrypts to identical bytes and no plaintext fingerprint is stored.
    /// The header MAC proves the key before any chunk is read; the table binds the payload, so a modified ciphertext byte is caught
    /// when its chunk is first read (lazily, <see cref="AmpbReadStream"/>). Proved by BundleCryptoTests (the golden container pins every
    /// byte of a small container).
    /// </summary>
    public static partial class Ampb
    {
        public const string MagicText = "AMPB";
        public const int FormatVersion = 1;
        public const int DefaultChunkSize = 1 << 20;
        public const int MinChunkSize = 4096;
        public const int MaxChunkSize = 16 << 20;
        public const int MaxMapKeyLength = 64;
        /// <summary>Bytes before the map key.</summary>
        public const int FixedHeaderSize = 42;
        public const int HashSize = 32;
        /// <summary>The table of a larger container is refused outright (4M chunks of 4 KiB = 16 GiB; the build uses 1 MiB chunks).</summary>
        internal const int MaxChunkCount = 1 << 22;

        static readonly byte[] Magic = { 0x41, 0x4D, 0x50, 0x42 };
        static readonly Encoding Ascii = Encoding.ASCII;

        /// <summary>True for a map key made of 1..64 characters of [a-z0-9_].</summary>
        public static bool IsValidMapKey(string mapKey)
        {
            if (string.IsNullOrEmpty(mapKey) || mapKey.Length > MaxMapKeyLength)
                return false;
            foreach (char c in mapKey)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
                    return false;
            return true;
        }

        /// <summary>True when the first four bytes of the stream are "AMPB" (the position is restored; false for a shorter stream).</summary>
        public static bool IsAmpb(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            long saved = stream.Position;
            try
            {
                stream.Position = 0;
                byte[] first = new byte[4];
                int got = ReadUpTo(stream, first, 4);
                return got == 4 && HasMagic(first);
            }
            finally
            {
                stream.Position = saved;
            }
        }

        /// <summary>True when the file starts with "AMPB". IO errors (missing file, sharing violation) propagate.</summary>
        public static bool IsAmpb(string path)
        {
            using (FileStream fs = OpenRead(path))
                return IsAmpb(fs);
        }

        /// <summary>Describes a container without any key: magic, structure, header fields, chunk table (not authenticated). Never throws for a defective file.</summary>
        public static AmpbInfo Inspect(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            try
            {
                AmpbHeader h = ParseStructure(stream);
                return new AmpbInfo(true, h, null);
            }
            catch (AmpbException ex)
            {
                return new AmpbInfo(ex.Kind != AmpbError.BadMagic, null, ex);
            }
        }

        public static AmpbInfo Inspect(string path)
        {
            using (FileStream fs = OpenRead(path))
                return Inspect(fs);
        }

        /// <summary>
        /// Reads the header and the chunk table and verifies the header MAC with the content key. Throws <see cref="AmpbException"/> for every
        /// defect, in this order: BadMagic, BadVersion, BadHeader/Truncated (structure and file length), HeaderAuth (wrong key or modified header),
        /// WrongMap (only when <paramref name="expectedMapKey"/> is given and differs). The stream's position afterwards is unspecified.
        /// </summary>
        public static AmpbHeader ReadHeader(Stream stream, byte[] kMap, ICryptoProvider provider, string expectedMapKey = null)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (provider == null) provider = CryptoProviders.Select(null);
            RequireKey(kMap);
            AmpbHeader h = ParseStructure(stream);
            AmpbKeys keys = AmpbKeys.Derive(kMap, provider);
            try
            {
                byte[] expected = provider.HmacSha256(keys.Mac, h.MacInput, 0, h.MacInput.Length);
                if (!ConstantTime.AreEqual(expected, h.Mac))
                    throw new AmpbException(AmpbError.HeaderAuth, "header authentication failed (not the bundle set of this key, or a modified file)");
            }
            finally
            {
                keys.Clear();
            }
            h.MacInput = null;
            h.Authenticated = true;
            if (expectedMapKey != null && !string.Equals(expectedMapKey, h.MapKey, StringComparison.Ordinal))
                throw new AmpbException(AmpbError.WrongMap, "the container belongs to '" + h.MapKey + "', not to '" + expectedMapKey + "'");
            return h;
        }

        /// <summary>
        /// Hashes every ciphertext chunk and compares it with the table (no key needed for the comparison itself; authenticate the header
        /// with <see cref="ReadHeader"/> first when the table must be trusted). Returns the number of chunks checked; throws
        /// <see cref="AmpbException"/> ChunkIntegrity for the first damaged chunk.
        /// </summary>
        public static int VerifyChunks(Stream stream, AmpbHeader header, ICryptoProvider provider)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (header == null) throw new ArgumentNullException("header");
            if (provider == null) provider = CryptoProviders.Select(null);
            if (header.ChunkHashes == null)
                throw new ArgumentException("the header carries no chunk table", "header");
            byte[] buffer = new byte[(int)Math.Min((long)header.ChunkSize, header.PlaintextLength)];
            for (int i = 0; i < header.ChunkCount; i++)
            {
                int length = header.GetChunkLength(i);
                ReadExactAt(stream, header.PayloadOffset + (long)i * header.ChunkSize, buffer, length);
                byte[] hash = provider.Sha256(buffer, 0, length);
                if (!HashEquals(hash, header.ChunkHashes, i * 32))
                    throw ChunkError(i, header);
            }
            return header.ChunkCount;
        }

        /// <summary>Opens a decrypting stream over a container file (the stream owns the file). The map key is taken from the container.</summary>
        public static AmpbReadStream Open(string path, byte[] kMap, ICryptoProvider provider = null, string expectedMapKey = null, int cacheSlots = 16)
        {
            if (cacheSlots < 1) throw new ArgumentOutOfRangeException("cacheSlots");
            FileStream fs = OpenRead(path);
            return new AmpbReadStream(fs, expectedMapKey, kMap, provider, cacheSlots, false);
        }

        /// <summary>Opens a decrypting stream over a seekable container stream (the decrypting stream owns it unless <paramref name="leaveOpen"/>).</summary>
        public static AmpbReadStream Open(Stream container, byte[] kMap, ICryptoProvider provider = null, string expectedMapKey = null, int cacheSlots = 16, bool leaveOpen = false)
        {
            return new AmpbReadStream(container, expectedMapKey, kMap, provider, cacheSlots, leaveOpen);
        }

        /// <summary>Decrypts a whole container into <paramref name="dest"/> (sequentially, every chunk verified) and returns the plaintext length.</summary>
        public static long Decrypt(Stream container, Stream dest, byte[] kMap, ICryptoProvider provider = null, string expectedMapKey = null)
        {
            if (dest == null) throw new ArgumentNullException("dest");
            using (AmpbReadStream plain = new AmpbReadStream(container, expectedMapKey, kMap, provider, 2, true))
            {
                byte[] buffer = new byte[(int)Math.Min((long)DefaultChunkSize, plain.Length)];
                long total = 0;
                int n;
                while ((n = plain.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dest.Write(buffer, 0, n);
                    total += n;
                }
                return total;
            }
        }

        // ---- structure ------------------------------------------------------------------------------------------

        /// <summary>Parses and validates everything that needs no key and reads the chunk table and the MAC field (the MAC is not checked here).</summary>
        internal static AmpbHeader ParseStructure(Stream stream)
        {
            if (!stream.CanRead || !stream.CanSeek)
                throw new ArgumentException("the container stream must be readable and seekable", "stream");
            long fileLength = stream.Length;
            byte[] fixedPart = new byte[FixedHeaderSize];
            stream.Position = 0;
            int got = ReadUpTo(stream, fixedPart, FixedHeaderSize);
            if (got < 4 || !HasMagic(fixedPart))
                throw new AmpbException(AmpbError.BadMagic, "not an AMPB container (the file does not start with \"AMPB\")");
            if (got < FixedHeaderSize)
                throw new AmpbException(AmpbError.Truncated, "truncated: " + fileLength + " bytes is shorter than the fixed header");

            int version = ReadU16(fixedPart, 4);
            int flags = ReadU16(fixedPart, 6);
            long chunkSizeValue = ReadU32(fixedPart, 8);
            long chunkCountValue = ReadU32(fixedPart, 12);
            ulong plainLengthValue = ReadU64(fixedPart, 16);
            ulong payloadOffsetValue = ReadU64(fixedPart, 24);
            byte[] nonce = new byte[8];
            Buffer.BlockCopy(fixedPart, 32, nonce, 0, 8);
            int keyLength = ReadU16(fixedPart, 40);

            if (version != FormatVersion)
                throw new AmpbException(AmpbError.BadVersion, "unsupported container version " + version);
            if (flags != 0)
                throw new AmpbException(AmpbError.BadVersion, "unsupported container flags 0x" + flags.ToString("x4"));
            if (chunkSizeValue < MinChunkSize || chunkSizeValue > MaxChunkSize || (chunkSizeValue & 15) != 0)
                throw new AmpbException(AmpbError.BadHeader, "chunk size " + chunkSizeValue + " is outside the accepted range");
            int chunkSize = (int)chunkSizeValue;
            if (plainLengthValue == 0 || plainLengthValue > long.MaxValue)
                throw new AmpbException(AmpbError.BadHeader, "plaintext length is zero or out of range");
            long plainLength = (long)plainLengthValue;
            long expectedCount = plainLength / chunkSize + (plainLength % chunkSize != 0 ? 1 : 0);
            if (chunkCountValue != expectedCount || chunkCountValue > MaxChunkCount)
                throw new AmpbException(AmpbError.BadHeader, "chunk count " + chunkCountValue + " does not match the plaintext length");
            int chunkCount = (int)chunkCountValue;
            if (keyLength < 1 || keyLength > MaxMapKeyLength)
                throw new AmpbException(AmpbError.BadHeader, "map key length " + keyLength + " is outside 1.." + MaxMapKeyLength);
            long tableOffset = (FixedHeaderSize + keyLength + 15) & ~15L;
            long expectedPayloadOffset = tableOffset + 32L * chunkCount + 32;
            if (payloadOffsetValue != (ulong)expectedPayloadOffset)
                throw new AmpbException(AmpbError.BadHeader, "payload offset does not match the table layout");
            long expectedFileLength = expectedPayloadOffset + plainLength;
            if (fileLength < expectedFileLength)
                throw new AmpbException(AmpbError.Truncated, "truncated: the file has " + fileLength + " bytes, the header says " + expectedFileLength);
            if (fileLength > expectedFileLength)
                throw new AmpbException(AmpbError.BadHeader, (fileLength - expectedFileLength) + " unexpected bytes after the payload");

            int regionLength = (int)(tableOffset + 32L * chunkCount);
            byte[] region = new byte[regionLength];
            Buffer.BlockCopy(fixedPart, 0, region, 0, FixedHeaderSize);
            ReadExactAt(stream, FixedHeaderSize, region, FixedHeaderSize, regionLength - FixedHeaderSize);
            byte[] mac = new byte[32];
            ReadExactAt(stream, regionLength, mac, 0, 32);

            for (int i = 0; i < keyLength; i++)
            {
                byte c = region[FixedHeaderSize + i];
                if (!((c >= (byte)'a' && c <= (byte)'z') || (c >= (byte)'0' && c <= (byte)'9') || c == (byte)'_'))
                    throw new AmpbException(AmpbError.BadHeader, "the map key contains a character outside [a-z0-9_]");
            }
            string mapKey = Ascii.GetString(region, FixedHeaderSize, keyLength);

            AmpbHeader h = new AmpbHeader(version, flags, chunkSize, chunkCount, plainLength, tableOffset, expectedPayloadOffset, fileLength, nonce, mapKey);
            byte[] table = new byte[32 * chunkCount];
            Buffer.BlockCopy(region, (int)tableOffset, table, 0, table.Length);
            h.ChunkHashes = table;
            h.MacInput = region;
            h.Mac = mac;
            return h;
        }

        internal static void RequireKey(byte[] kMap)
        {
            if (kMap == null || kMap.Length != 32)
                throw new ArgumentException("the content key K_map is 32 bytes", "kMap");
        }

        internal static AmpbException ChunkError(int chunk, AmpbHeader h)
        {
            long offset = (long)chunk * h.ChunkSize;
            return new AmpbException(AmpbError.ChunkIntegrity, chunk,
                "chunk " + chunk + " (offset " + offset + ") failed its integrity check; the file is damaged");
        }

        internal static bool HashEquals(byte[] hash, byte[] table, int tableOffset)
        {
            int diff = 0;
            for (int i = 0; i < 32; i++)
                diff |= hash[i] ^ table[tableOffset + i];
            return diff == 0;
        }

        static bool HasMagic(byte[] first)
        {
            return first[0] == Magic[0] && first[1] == Magic[1] && first[2] == Magic[2] && first[3] == Magic[3];
        }

        internal static FileStream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        }

        /// <summary>Reads up to <paramref name="count"/> bytes (fewer only at the end of the stream).</summary>
        internal static int ReadUpTo(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, total, count - total);
                if (n <= 0)
                    break;
                total += n;
            }
            return total;
        }

        /// <summary>Reads exactly <paramref name="count"/> bytes at an absolute position; a short file is a Truncated container.</summary>
        internal static void ReadExactAt(Stream stream, long position, byte[] buffer, int count)
        {
            ReadExactAt(stream, position, buffer, 0, count);
        }

        internal static void ReadExactAt(Stream stream, long position, byte[] buffer, int offset, int count)
        {
            stream.Position = position;
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, offset + total, count - total);
                if (n <= 0)
                    throw new AmpbException(AmpbError.Truncated, "truncated: the file ended " + (count - total) + " bytes early at offset " + (position + total));
                total += n;
            }
        }

        static int ReadU16(byte[] b, int o)
        {
            return b[o] | (b[o + 1] << 8);
        }

        static long ReadU32(byte[] b, int o)
        {
            return (long)(uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        static ulong ReadU64(byte[] b, int o)
        {
            ulong lo = (ulong)ReadU32(b, o);
            ulong hi = (ulong)ReadU32(b, o + 4);
            return lo | (hi << 32);
        }

        internal static void WriteU16(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        internal static void WriteU32(byte[] b, int o, long v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        internal static void WriteU64(byte[] b, int o, long v)
        {
            WriteU32(b, o, v & 0xFFFFFFFFL);
            WriteU32(b, o + 4, (v >> 32) & 0xFFFFFFFFL);
        }
    }

    /// <summary>
    /// The three container subkeys derived from the content key K_map (HKDF-Expand with PRK = K_map, one block each, info = "ArenaMapPort
    /// bundle enc v1" etc.). Separate keys for the cipher, the MAC and the nonce, so no key is used for two purposes. Proved by the golden
    /// container test (any change here changes the pinned SHA-256).
    /// </summary>
    internal sealed class AmpbKeys
    {
        public byte[] Enc;
        public byte[] Mac;
        public byte[] Nonce;

        public static AmpbKeys Derive(byte[] kMap, ICryptoProvider provider)
        {
            Ampb.RequireKey(kMap);
            AmpbKeys k = new AmpbKeys();
            k.Enc = Hkdf.Expand(provider, kMap, Encoding.ASCII.GetBytes("ArenaMapPort bundle enc v1"), 32);
            k.Mac = Hkdf.Expand(provider, kMap, Encoding.ASCII.GetBytes("ArenaMapPort bundle mac v1"), 32);
            k.Nonce = Hkdf.Expand(provider, kMap, Encoding.ASCII.GetBytes("ArenaMapPort bundle nonce v1"), 32);
            return k;
        }

        public void Clear()
        {
            if (Enc != null) Array.Clear(Enc, 0, Enc.Length);
            if (Mac != null) Array.Clear(Mac, 0, Mac.Length);
            if (Nonce != null) Array.Clear(Nonce, 0, Nonce.Length);
        }
    }
}
