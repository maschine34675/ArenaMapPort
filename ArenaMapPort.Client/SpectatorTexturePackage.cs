#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArenaMapPort.Crypto;
using Newtonsoft.Json;

namespace ArenaMapPort.Client
{
    // The map pins its shared manifest, which pins the exact PNG bytes. Mixed core/map updates fail closed.
    // Read() is safe to call from several threads (the manifest is immutable after construction; the counters are interlocked): the crowd
    // loader verifies the files on a worker thread (SpectatorTexturePrefetch) and the main thread only decodes them. The SHA-256 runs
    // through the provider the bundle decryption already uses (Windows CNG, managed only as the silent fallback): on EFT's Mono the
    // managed SHA256 needed about 2 s for the 146,689,024 bytes of the 123 shared PNGs, one block of the main thread per raid.
    internal sealed class SpectatorTexturePackage
    {
        readonly Dictionary<string, Entry> _files = new Dictionary<string, Entry>(StringComparer.Ordinal);
        readonly ICryptoProvider _crypto;
        long _readTicks, _hashTicks;

        public SpectatorTexturePackage(byte[] manifest, string expectedHash) : this(manifest, expectedHash, null) { }

        /// <param name="crypto">The SHA-256 provider; null selects the process-wide one (CNG when it works, else managed).</param>
        public SpectatorTexturePackage(byte[] manifest, string expectedHash, ICryptoProvider crypto)
        {
            _crypto = crypto ?? CryptoProviders.Select(null);
            if (manifest == null || Hash(manifest) != expectedHash) throw new InvalidDataException("spectator map/shared manifest mismatch");
            var data = JsonConvert.DeserializeObject<Manifest>(Encoding.UTF8.GetString(manifest));
            if (data == null || data.schema != 1 || data.files == null || data.files.Length < 1 || data.files.Length > 4096)
                throw new InvalidDataException("unsupported shared spectator textures");
            foreach (var row in data.files)
            {
                if (row == null || row.file == null || !Regex.IsMatch(row.file, @"\A[0-9a-f]{16}(?:-[0-5])?\.png\z") ||
                    row.bytes <= 0 || row.bytes > 128 * 1024 * 1024 || row.sha256 == null ||
                    !Regex.IsMatch(row.sha256, @"\A[0-9a-f]{64}\z") || _files.ContainsKey(row.file))
                    throw new InvalidDataException("invalid shared spectator texture entry");
                _files.Add(row.file, row);
            }
        }

        /// <summary>"CNG" or "managed": the provider that hashes the files (for the Debug summary).</summary>
        public string HashProvider => _crypto.Name;

        /// <summary>Time spent reading the files, summed over every Read call (any thread).</summary>
        public double ReadMilliseconds => Interlocked.Read(ref _readTicks) * 1000.0 / Stopwatch.Frequency;

        /// <summary>Time spent hashing the files, summed over every Read call (any thread).</summary>
        public double HashMilliseconds => Interlocked.Read(ref _hashTicks) * 1000.0 / Stopwatch.Frequency;

        public byte[] Read(string folder, string file)
        {
            if (file == null || !_files.TryGetValue(file, out var row))
                throw new InvalidDataException("unlisted spectator texture");
            var path = Path.Combine(folder, file);
            if (new FileInfo(path).Length != row.bytes) throw new InvalidDataException("damaged spectator texture: " + file);
            long start = Stopwatch.GetTimestamp();
            var bytes = File.ReadAllBytes(path);
            long read = Stopwatch.GetTimestamp();
            // Fail closed: a size or digest that differs from the manifest is a damaged or mixed install, never a partial success.
            bool intact = bytes.Length == row.bytes && Hash(bytes) == row.sha256;
            long done = Stopwatch.GetTimestamp();
            Interlocked.Add(ref _readTicks, read - start);
            Interlocked.Add(ref _hashTicks, done - read);
            if (!intact) throw new InvalidDataException("damaged spectator texture: " + file);
            return bytes;
        }

        string Hash(byte[] bytes) => HexCodec.ToHex(_crypto.Sha256(bytes, 0, bytes.Length));

        #pragma warning disable CS0649
        sealed class Manifest { public int schema; public Entry[] files; }
        sealed class Entry { public string file, sha256; public int bytes; }
        #pragma warning restore CS0649
    }

    /// <summary>
    /// Reads and verifies the texture files of one crowd on a worker thread, a bounded distance ahead of the main thread that decodes them.
    /// The main thread calls <see cref="Take"/> with the files in the order it uses them; the worker keeps at most about
    /// <c>budget</c> bytes of verified files waiting (the 146 MB of a crowd are never all in memory), and a file that fails
    /// <see cref="SpectatorTexturePackage.Read"/> stops the worker and surfaces from <see cref="Take"/> once the files before it were taken. The worker
    /// is plain managed code: no Unity call and no logging. A consumer that gives up calls <see cref="Dispose"/> (the worker exits at its next check).
    /// </summary>
    internal sealed class SpectatorTexturePrefetch : IDisposable
    {
        public const long DefaultBudgetBytes = 48L * 1024 * 1024;
        public const int DefaultTimeoutMs = 30000;
        /// <summary>A worker whose consumer never comes back (a raid that died) stops after this long instead of waiting forever.</summary>
        const int IdleLimitMs = 10 * 60 * 1000;

        struct Slot
        {
            public string File;
            public byte[] Bytes;
        }

        readonly SpectatorTexturePackage _package;
        readonly string _folder;
        readonly string[] _order;
        readonly long _budget;
        readonly object _gate = new object();
        readonly Queue<Slot> _ready = new Queue<Slot>();
        long _readyBytes, _waitTicks;
        bool _started, _complete, _cancelled;
        Exception _error;
        Task _task;

        public SpectatorTexturePrefetch(SpectatorTexturePackage package, string folder, string[] order, long budgetBytes)
        {
            if (package == null) throw new ArgumentNullException("package");
            if (order == null) throw new ArgumentNullException("order");
            _package = package;
            _folder = folder;
            _order = order;
            _budget = budgetBytes > 0 ? budgetBytes : DefaultBudgetBytes;
        }

        /// <summary>Time the consumer spent blocked in <see cref="Take"/> waiting for the worker.</summary>
        public double WaitMilliseconds
        {
            get { lock (_gate) return _waitTicks * 1000.0 / Stopwatch.Frequency; }
        }

        /// <summary>Verified bytes waiting for the consumer (bounded by the budget plus the file that crossed it).</summary>
        public long PendingBytes
        {
            get { lock (_gate) return _readyBytes; }
        }

        /// <summary>The worker task (null before <see cref="Start"/>); it never faults: errors are kept for <see cref="Take"/>.</summary>
        public Task Worker => _task;

        public void Start()
        {
            lock (_gate)
            {
                if (_started || _cancelled) return;
                _started = true;
                _task = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        void Run()
        {
            try
            {
                for (int i = 0; i < _order.Length; i++)
                {
                    lock (_gate)
                    {
                        // Wait for room, unless nothing is waiting (a single file larger than the budget still has to pass).
                        while (!_cancelled && _ready.Count > 0 && _readyBytes >= _budget)
                        {
                            if (!Monitor.Wait(_gate, IdleLimitMs))
                            {
                                _error = new TimeoutException("the texture consumer did not come back");
                                Monitor.PulseAll(_gate);
                                return;
                            }
                        }
                        if (_cancelled) return;
                    }
                    byte[] bytes = _package.Read(_folder, _order[i]);
                    lock (_gate)
                    {
                        if (_cancelled) return;
                        _ready.Enqueue(new Slot { File = _order[i], Bytes = bytes });
                        _readyBytes += bytes.Length;
                        Monitor.PulseAll(_gate);
                    }
                }
                lock (_gate)
                {
                    _complete = true;
                    Monitor.PulseAll(_gate);
                }
            }
            catch (Exception e)
            {
                lock (_gate)
                {
                    _error = e;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        /// <summary>
        /// The verified bytes of <paramref name="file"/>, which must be the next file of the order given to the constructor. Blocks (at most
        /// <paramref name="timeoutMs"/>) until the worker has it. Throws <see cref="InvalidOperationException"/> when the worker failed, the order
        /// differs, the wait timed out or the prefetch was disposed: the caller then reads the file itself.
        /// </summary>
        public byte[] Take(string file, int timeoutMs)
        {
            lock (_gate)
            {
                long begin = Stopwatch.GetTimestamp();
                try
                {
                    while (_ready.Count == 0)
                    {
                        if (_cancelled) throw new ObjectDisposedException("SpectatorTexturePrefetch");
                        if (_error != null)
                            throw new InvalidOperationException("background texture load failed (" + _error.GetType().Name + ": " + _error.Message + ")", _error);
                        if (_complete) throw new InvalidOperationException("no further texture was scheduled: " + file);
                        long left = timeoutMs - (Stopwatch.GetTimestamp() - begin) * 1000 / Stopwatch.Frequency;
                        if (left <= 0 || !Monitor.Wait(_gate, (int)Math.Min(left, int.MaxValue)))
                            throw new InvalidOperationException("background texture load timed out waiting for " + file);
                    }
                    Slot slot = _ready.Dequeue();
                    _readyBytes -= slot.Bytes.Length;
                    Monitor.PulseAll(_gate);
                    if (!string.Equals(slot.File, file, StringComparison.Ordinal))
                    {
                        _cancelled = true;
                        _ready.Clear();
                        _readyBytes = 0;
                        throw new InvalidOperationException("background texture order differs: expected " + slot.File + ", asked " + file);
                    }
                    return slot.Bytes;
                }
                finally
                {
                    _waitTicks += Stopwatch.GetTimestamp() - begin;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _cancelled = true;
                _ready.Clear();
                _readyBytes = 0;
                Monitor.PulseAll(_gate);
            }
        }
    }
}
