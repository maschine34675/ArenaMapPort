#nullable disable
using System;
using System.Diagnostics;
using System.IO;

namespace ArenaMapPort.Crypto
{
    /// <summary>Counters of an <see cref="AmpbReadStream"/> (a snapshot): what the Debug line "stream ... decrypted" reports.</summary>
    public sealed class AmpbStats
    {
        internal AmpbStats(long bytesDecrypted, long chunksDecrypted, long chunksVerified, long cacheHits, long cacheMisses, double cryptoMilliseconds, double readMilliseconds)
        {
            BytesDecrypted = bytesDecrypted;
            ChunksDecrypted = chunksDecrypted;
            ChunksVerified = chunksVerified;
            CacheHits = cacheHits;
            CacheMisses = cacheMisses;
            CryptoMilliseconds = cryptoMilliseconds;
            ReadMilliseconds = readMilliseconds;
        }

        /// <summary>Plaintext bytes produced by decryption, counting a chunk again each time it is decrypted after eviction (the amplification the budget measures).</summary>
        public long BytesDecrypted { get; private set; }
        public long ChunksDecrypted { get; private set; }
        /// <summary>Chunks whose ciphertext hash was checked against the table (each chunk once).</summary>
        public long ChunksVerified { get; private set; }
        public long CacheHits { get; private set; }
        public long CacheMisses { get; private set; }
        /// <summary>Time spent hashing and decrypting chunks.</summary>
        public double CryptoMilliseconds { get; private set; }
        /// <summary>Time spent reading ciphertext from the underlying stream.</summary>
        public double ReadMilliseconds { get; private set; }
    }

    /// <summary>
    /// A seekable, read-only stream over the plaintext of an AMPB container, decrypting on demand. Why: AssetBundle.LoadFromStream keeps the
    /// stream for the bundle's lifetime and reads it from Unity's loading thread at arbitrary offsets, so the stream must be seekable, report
    /// the plaintext Length and be safe against concurrent callers: ONE lock guards the position, the cache and the AES key handle.
    /// Reading: the header MAC is verified in the constructor (a wrong key fails there, before any chunk is read); a read pulls whole chunks
    /// through a small LRU cache (<c>min(chunkCount, cacheSlots)</c> slots of one chunk, allocated lazily and reused, so no per-read large
    /// allocation); on a miss the ciphertext chunk is read, its SHA-256 is compared with the table (once per chunk for the stream's life, so
    /// verification is lazy and a chunk evicted and read again is only decrypted again) and decrypted in place. A damaged chunk raises
    /// <see cref="AmpbException"/> ChunkIntegrity for the reads that touch it and for no others; the first such error is kept in
    /// <see cref="FirstError"/> so the owner can log it later from the game thread (this class never logs). The stream owns the source
    /// unless <c>leaveOpen</c>; Unity never closes it, so the process keeps the handle. Proved by BundleCryptoTests (round trips, seek and
    /// read patterns, 2-slot eviction, lazy verification with a counting source, corrupt chunk isolation, 8 threads of random reads).
    /// </summary>
    public sealed class AmpbReadStream : Stream
    {
        readonly object _lock = new object();
        readonly Stream _src;
        readonly bool _leaveOpen;
        readonly AmpbHeader _h;
        readonly IAesKey _aes;
        readonly byte[] _nonce;
        readonly byte[] _table;
        readonly ICryptoProvider _p;
        readonly int _chunkSize;
        readonly byte[][] _slotData;
        readonly int[] _slotChunk;
        readonly long[] _slotUse;
        readonly bool[] _verified;
        readonly int _slotCapacity;
        long _pos;
        long _tick;
        bool _disposed;
        AmpbException _firstError;
        long _bytesDecrypted, _chunksDecrypted, _chunksVerified, _hits, _misses, _cryptoTicks, _readTicks;

        /// <summary>
        /// Opens the container in <paramref name="src"/> with the content key. <paramref name="expectedMapKey"/> (null accepts the container's own)
        /// is compared after the MAC. Throws <see cref="AmpbException"/> for a defective container; the source is closed in that case
        /// unless <paramref name="leaveOpen"/>.
        /// </summary>
        public AmpbReadStream(Stream src, string expectedMapKey, byte[] kMap, ICryptoProvider provider, int cacheSlots = 16, bool leaveOpen = false)
        {
            if (src == null) throw new ArgumentNullException("src");
            if (cacheSlots < 1) throw new ArgumentOutOfRangeException("cacheSlots");
            _src = src;
            _leaveOpen = leaveOpen;
            _p = provider ?? CryptoProviders.Select(null);
            try
            {
                _h = Ampb.ReadHeader(src, kMap, _p, expectedMapKey);
                AmpbKeys keys = AmpbKeys.Derive(kMap, _p);
                try
                {
                    _aes = _p.NewAes(keys.Enc);
                }
                finally
                {
                    keys.Clear();
                }
            }
            catch (Exception)
            {
                if (!leaveOpen)
                    src.Dispose();
                throw;
            }
            _nonce = _h.NonceBytes;
            _table = _h.ChunkHashes;
            _chunkSize = _h.ChunkSize;
            int slots = Math.Min(_h.ChunkCount, cacheSlots);
            _slotData = new byte[slots][];
            _slotChunk = new int[slots];
            _slotUse = new long[slots];
            for (int i = 0; i < slots; i++)
                _slotChunk[i] = -1;
            _verified = new bool[_h.ChunkCount];
            _slotCapacity = (int)Math.Min((long)_chunkSize, _h.PlaintextLength);
        }

        /// <summary>The authenticated header (map key, plaintext length, chunk size and count).</summary>
        public AmpbHeader Header
        {
            get { return _h; }
        }

        /// <summary>A snapshot of the counters.</summary>
        public AmpbStats Stats
        {
            get
            {
                lock (_lock)
                {
                    double toMs = 1000.0 / Stopwatch.Frequency;
                    return new AmpbStats(_bytesDecrypted, _chunksDecrypted, _chunksVerified, _hits, _misses, _cryptoTicks * toMs, _readTicks * toMs);
                }
            }
        }

        /// <summary>The first integrity error a read raised (null while none did). Set once, readable from any thread.</summary>
        public AmpbException FirstError
        {
            get { lock (_lock) return _firstError; }
        }

        public override bool CanRead { get { return !_disposed; } }
        public override bool CanSeek { get { return !_disposed; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _h.PlaintextLength; } }

        public override long Position
        {
            get { lock (_lock) { ThrowIfDisposed(); return _pos; } }
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException("value");
                lock (_lock)
                {
                    ThrowIfDisposed();
                    _pos = value;
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                long target;
                switch (origin)
                {
                    case SeekOrigin.Begin: target = offset; break;
                    case SeekOrigin.Current: target = _pos + offset; break;
                    case SeekOrigin.End: target = _h.PlaintextLength + offset; break;
                    default: throw new ArgumentException("invalid seek origin", "origin");
                }
                if (target < 0)
                    throw new IOException("An attempt was made to move the position before the beginning of the stream.");
                _pos = target;
                return target;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateBuffer(buffer, offset, count);
            lock (_lock)
            {
                ThrowIfDisposed();
                int n = ReadLocked(_pos, buffer, offset, count);
                _pos += n;
                return n;
            }
        }

        /// <summary>
        /// Reads at an absolute plaintext position without touching <see cref="Position"/>, atomically under the stream's lock (a Seek followed by a
        /// Read is two lock acquisitions, so concurrent callers that share the position can interleave; this call cannot). Returns fewer bytes only at the end.
        /// </summary>
        public int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            if (position < 0) throw new ArgumentOutOfRangeException("position");
            ValidateBuffer(buffer, offset, count);
            lock (_lock)
            {
                ThrowIfDisposed();
                return ReadLocked(position, buffer, offset, count);
            }
        }

        static void ValidateBuffer(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0) throw new ArgumentOutOfRangeException("offset");
            if (count < 0) throw new ArgumentOutOfRangeException("count");
            if (buffer.Length - offset < count) throw new ArgumentException("offset and count exceed the buffer");
        }

        /// <summary>The read loop, with the lock held: copies across chunk boundaries out of the cache.</summary>
        int ReadLocked(long position, byte[] buffer, int offset, int count)
        {
            long length = _h.PlaintextLength;
            int total = 0;
            while (count > 0 && position < length)
            {
                int chunk = (int)(position / _chunkSize);
                int within = (int)(position - (long)chunk * _chunkSize);
                byte[] data = GetChunk(chunk);
                int available = _h.GetChunkLength(chunk) - within;
                int n = Math.Min(count, available);
                Buffer.BlockCopy(data, within, buffer, offset, n);
                position += n;
                offset += n;
                count -= n;
                total += n;
            }
            return total;
        }

        public override int ReadByte()
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                if (_pos >= _h.PlaintextLength)
                    return -1;
                int chunk = (int)(_pos / _chunkSize);
                int within = (int)(_pos - (long)chunk * _chunkSize);
                byte b = GetChunk(chunk)[within];
                _pos++;
                return b;
            }
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lock)
                {
                    if (_disposed)
                        return;
                    _disposed = true;
                    _aes.Dispose();
                    for (int i = 0; i < _slotData.Length; i++)
                    {
                        _slotData[i] = null;
                        _slotChunk[i] = -1;
                    }
                    if (!_leaveOpen)
                        _src.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("AmpbReadStream");
        }

        /// <summary>Returns the cache slot holding the decrypted chunk (loading it on a miss). Called with the lock held.</summary>
        byte[] GetChunk(int chunk)
        {
            _tick++;
            for (int i = 0; i < _slotChunk.Length; i++)
            {
                if (_slotChunk[i] == chunk)
                {
                    _slotUse[i] = _tick;
                    _hits++;
                    return _slotData[i];
                }
            }

            _misses++;
            int slot = 0;
            long oldest = long.MaxValue;
            for (int i = 0; i < _slotChunk.Length; i++)
            {
                if (_slotChunk[i] < 0)
                {
                    slot = i;
                    break;
                }
                if (_slotUse[i] < oldest)
                {
                    oldest = _slotUse[i];
                    slot = i;
                }
            }
            if (_slotData[slot] == null)
                _slotData[slot] = new byte[_slotCapacity];
            byte[] data = _slotData[slot];
            _slotChunk[slot] = -1;   // invalid while it is being filled: a failure must not leave half a chunk cached

            int length = _h.GetChunkLength(chunk);
            long t0 = Stopwatch.GetTimestamp();
            Ampb.ReadExactAt(_src, _h.PayloadOffset + (long)chunk * _chunkSize, data, length);
            long t1 = Stopwatch.GetTimestamp();
            if (!_verified[chunk])
            {
                byte[] hash = _p.Sha256(data, 0, length);
                if (!Ampb.HashEquals(hash, _table, chunk * 32))
                {
                    AmpbException error = Ampb.ChunkError(chunk, _h);
                    if (_firstError == null)
                        _firstError = error;
                    _readTicks += t1 - t0;
                    _cryptoTicks += Stopwatch.GetTimestamp() - t1;
                    throw error;
                }
                _verified[chunk] = true;
                _chunksVerified++;
            }
            AesCtr.Transform(_aes, _nonce, (ulong)((long)chunk * _chunkSize / 16), data, 0, data, 0, length);
            long t2 = Stopwatch.GetTimestamp();
            _readTicks += t1 - t0;
            _cryptoTicks += t2 - t1;
            _bytesDecrypted += length;
            _chunksDecrypted++;

            _slotChunk[slot] = chunk;
            _slotUse[slot] = _tick;
            return data;
        }
    }
}
