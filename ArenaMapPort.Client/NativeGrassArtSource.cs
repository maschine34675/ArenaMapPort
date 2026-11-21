#nullable disable
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArenaMapPort.Crypto;

namespace ArenaMapPort.Client
{
    // The file side of the native grass probe (PLAN-YARD item 23, shape E; lead decision 2026-11-21): the placement records and the three art ranges are read from the player's VERIFIED
    // Arena installation on a worker thread, checked with the Unity-free models (NativeGrassPlacement, NativeGrassArt), and handed to the main thread in a plain object. Everything here runs
    // off the main thread, so nothing in this file touches a Unity type, the plugin or the log (the crowd worker of ArenaSpectators is the precedent): a failure travels as a reason CODE in
    // GrassInputs.Error, never a path or file content. Files open read-only with sharing that cannot block Arena or its launcher (DirectoryArenaFiles: FileShare.ReadWrite | Delete). The
    // bytes live in memory only: nothing is written, hashed for a log, cached or logged; the glue drops the art buffers right after the upload (GrassInputs.DropArt). The Arena installation
    // check is not touched: the glue hands over the folder the gate already verified.

    /// <summary>Where the art bytes come from (developer key NativeGrassArt).</summary>
    internal enum GrassArtKind
    {
        /// <summary>The three ranges of Arena's sharedassets95.assets.resS, by the pinned offsets and sizes.</summary>
        Arena = 0,
        /// <summary>The BSG-free placeholder art of <see cref="NativeGrassArt.Placeholder"/> (no Arena art byte is read; the placement file still is).</summary>
        Placeholder = 1,
    }

    /// <summary>One way to get the bytes of a texture range. Two implementations: Arena's own file and the placeholder generator. Called on the worker thread only.</summary>
    internal interface IGrassArtSource
    {
        string Name { get; }
        /// <summary>The checks that need no range: the two file sizes for Arena's file, nothing for the placeholder.</summary>
        GrassArtError CheckFiles();
        /// <summary>The bytes of <paramref name="range"/> (exactly <see cref="GrassArtRange.Size"/> long), or an error and null.</summary>
        GrassArtError Read(GrassArtRange range, out byte[] data);
        /// <summary>How many file bytes were read so far (0 for the placeholder).</summary>
        long BytesRead { get; }
    }

    /// <summary>Arena's stream by the pinned offsets: sizes of both files first, then one bounded read per range.</summary>
    internal sealed class ArenaFilesArtSource : IGrassArtSource
    {
        readonly IArenaFiles _files;
        long _bytesRead;

        internal ArenaFilesArtSource(IArenaFiles files)
        {
            _files = files ?? throw new ArgumentNullException(nameof(files));
        }

        public string Name => "Arena";

        public long BytesRead => _bytesRead;

        public GrassArtError CheckFiles()
        {
            return NativeGrassArt.CheckFileSizes(_files.Length(NativeGrassArt.AssetsPath), _files.Length(NativeGrassArt.StreamPath));
        }

        public GrassArtError Read(GrassArtRange range, out byte[] data)
        {
            data = null;
            if (range.Size <= 0 || range.Size > int.MaxValue)
            {
                return GrassArtError.RangeArithmetic;
            }
            var buffer = new byte[(int)range.Size];
            using (Stream stream = _files.Open(NativeGrassArt.StreamPath))
            {
                if (range.Offset < 0 || range.Offset + range.Size > stream.Length)
                {
                    return GrassArtError.RangeOutsideFile;
                }
                stream.Seek(range.Offset, SeekOrigin.Begin);
                int done = 0;
                while (done < buffer.Length)
                {
                    int n = stream.Read(buffer, done, buffer.Length - done);
                    if (n <= 0)
                    {
                        return GrassArtError.ShortRead;
                    }
                    done += n;
                }
            }
            _bytesRead += buffer.Length;
            data = buffer;
            return GrassArtError.None;
        }
    }

    /// <summary>The placeholder art: generated blocks of the same shape (a solid colour and a cut-out per slice), no file access.</summary>
    internal sealed class PlaceholderArtSource : IGrassArtSource
    {
        public string Name => "Placeholder";

        public long BytesRead => 0;

        public GrassArtError CheckFiles() => GrassArtError.None;

        public GrassArtError Read(GrassArtRange range, out byte[] data)
        {
            var buffer = new byte[(int)range.Size];
            NativeGrassArt.Placeholder.Fill(buffer, range);
            data = buffer;
            return GrassArtError.None;
        }
    }

    /// <summary>
    /// What the worker hands to the main thread: the parsed placement plan and the three raw block buffers, or a reason code. Plain data, no Unity type. <see cref="DropArt"/> releases the
    /// art buffers (the glue calls it right after the upload); the plan stays until the preparation is dropped.
    /// </summary>
    internal sealed class GrassInputs
    {
        /// <summary>null = ready; otherwise a code such as "placement:CountOutOfRange", "art-size:StreamSizeDiffers", "art-blocks:GrassTexArray:MipChainIncoherent", "io:IOException".</summary>
        internal string Error;
        internal GrassPlan Plan;
        internal int PlacementLength;
        internal byte[] NormalTexture;
        internal byte[] TexArray;
        internal byte[] NormalArray;
        internal string ArtName;
        /// <summary>Codes only, for the Debug line: "sizes=ok ranges=ok blocks=3/3".</summary>
        internal string Checks;
        internal long PlacementTicks;
        internal long ArtTicks;
        internal long BytesRead;

        internal bool Ok => Error == null;

        internal void DropArt()
        {
            NormalTexture = null;
            TexArray = null;
            NormalArray = null;
        }

        internal static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }

    /// <summary>
    /// One preparation of a map's grass inputs on a worker thread (a LongRunning task like the crowd's), started by the glue when the grass is wanted in an owned raid. The task body never throws
    /// and never logs; <see cref="IsCompleted"/> is a cheap poll for the tick (the main thread never blocks on it: the glue's watchdog turns a worker that does not finish in time into an input
    /// error). <see cref="Abandon"/> asks the worker to stop between two files.
    /// </summary>
    internal sealed class GrassPreparation
    {
        readonly Task<GrassInputs> _task;
        volatile bool _abandoned;

        GrassPreparation(Func<GrassPreparation, GrassInputs> work)
        {
            _task = Task.Factory.StartNew(() => work(this), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        /// <summary>Starts the worker. <paramref name="installPath"/> is the folder of the installation the gate verified.</summary>
        internal static GrassPreparation Start(NativeGrassMapRow row, string installPath, GrassArtKind kind)
        {
            return new GrassPreparation(self => Load(self, row, new DirectoryArenaFiles(installPath), kind));
        }

        /// <summary>The same work on an injected file layer (tests).</summary>
        internal static GrassPreparation Start(NativeGrassMapRow row, IArenaFiles files, GrassArtKind kind)
        {
            return new GrassPreparation(self => Load(self, row, files, kind));
        }

        internal bool IsCompleted => _task.IsCompleted;

        internal bool Abandoned => _abandoned;

        /// <summary>The finished result, or null while the worker runs (a faulted task cannot happen: the body catches everything, but the poll stays safe).</summary>
        internal GrassInputs Result
        {
            get
            {
                if (!_task.IsCompleted)
                {
                    return null;
                }
                if (_task.IsFaulted || _task.IsCanceled)
                {
                    return new GrassInputs { Error = "worker:faulted" };
                }
                return _task.Result;
            }
        }

        internal void Abandon()
        {
            _abandoned = true;
        }

        /// <summary>The whole load, synchronously: also what the tests call. Never throws.</summary>
        internal static GrassInputs Load(GrassPreparation owner, NativeGrassMapRow row, IArenaFiles files, GrassArtKind kind)
        {
            var inputs = new GrassInputs();
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                if (!ReadPlacement(row, files, inputs))
                {
                    return inputs;
                }
                inputs.PlacementTicks = Stopwatch.GetTimestamp() - t0;
                if (owner != null && owner.Abandoned)
                {
                    inputs.Error = "worker:abandoned";
                    return inputs;
                }

                long t1 = Stopwatch.GetTimestamp();
                IGrassArtSource source = kind == GrassArtKind.Placeholder ? (IGrassArtSource)new PlaceholderArtSource() : new ArenaFilesArtSource(files);
                inputs.ArtName = source.Name;
                GrassArtError error = NativeGrassArt.CheckRanges();
                if (error != GrassArtError.None)
                {
                    inputs.Error = "art-ranges:" + error;
                    return inputs;
                }
                error = source.CheckFiles();
                if (error != GrassArtError.None)
                {
                    inputs.Error = "art-size:" + error;
                    return inputs;
                }
                int blocksOk = 0;
                foreach (GrassArtRange range in NativeGrassArt.Ranges)
                {
                    if (owner != null && owner.Abandoned)
                    {
                        inputs.Error = "worker:abandoned";
                        return inputs;
                    }
                    byte[] data;
                    error = source.Read(range, out data);
                    if (error != GrassArtError.None)
                    {
                        inputs.Error = "art-read:" + range.Name + ":" + error;
                        return inputs;
                    }
                    // the structure check is for Arena's bytes: the generated placeholder is correct by construction and has no coherent mip chain to test
                    if (kind == GrassArtKind.Arena)
                    {
                        error = NativeGrassArt.CheckBlocks(data, range);
                        if (error != GrassArtError.None)
                        {
                            inputs.Error = "art-blocks:" + range.Name + ":" + error;
                            return inputs;
                        }
                        blocksOk++;
                    }
                    if (ReferenceEquals(range, NativeGrassArt.NormalTexture)) inputs.NormalTexture = data;
                    else if (ReferenceEquals(range, NativeGrassArt.TexArray)) inputs.TexArray = data;
                    else inputs.NormalArray = data;
                }
                inputs.BytesRead = source.BytesRead;
                inputs.ArtTicks = Stopwatch.GetTimestamp() - t1;
                inputs.Checks = kind == GrassArtKind.Arena
                    ? "sizes=ok ranges=ok blocks=" + blocksOk.ToString(CultureInfo.InvariantCulture) + "/" + NativeGrassArt.Ranges.Length.ToString(CultureInfo.InvariantCulture)
                    : "generated, no file read";
                return inputs;
            }
            catch (Exception e)
            {
                // the code carries the exception TYPE only: a message can hold a path of the player's disk
                inputs.Error = "io:" + e.GetType().Name;
                inputs.DropArt();
                return inputs;
            }
        }

        static bool ReadPlacement(NativeGrassMapRow row, IArenaFiles files, GrassInputs inputs)
        {
            long length = files.Length(row.PlacementRelativePath);
            if (length < 0)
            {
                inputs.Error = "placement:Missing";
                return false;
            }
            if (length == 0 || length > int.MaxValue || length > NativeGrassPlacement.AbsoluteMaxRecords * (long)NativeGrassPlacement.RecordSize)
            {
                inputs.Error = "placement:BadLength";
                return false;
            }
            var bytes = new byte[(int)length];
            using (Stream stream = files.Open(row.PlacementRelativePath))
            {
                int done = 0;
                while (done < bytes.Length)
                {
                    int n = stream.Read(bytes, done, bytes.Length - done);
                    if (n <= 0)
                    {
                        inputs.Error = "placement:ShortRead";
                        return false;
                    }
                    done += n;
                }
            }
            GrassParseResult parsed = NativeGrassPlacement.Parse(bytes, bytes.Length, row);
            if (!parsed.Ok)
            {
                inputs.Error = "placement:" + parsed.Error + (parsed.ErrorIndex >= 0 ? "@" + parsed.ErrorIndex.ToString(CultureInfo.InvariantCulture) : string.Empty);
                return false;
            }
            inputs.Plan = parsed.Plan;
            inputs.PlacementLength = bytes.Length;
            return true;
        }
    }
}
