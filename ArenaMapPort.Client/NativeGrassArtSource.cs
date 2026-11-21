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
    // The file side of the native grass (PLAN-YARD item 23; since stage 2, 2026-11-21, the data is the map's staged grass bundle bundles/<map_key>_grass.bundle, not Arena's installation: nothing
    // in grass code reads an Arena file or asks the installation gate). The bundle is an AMPB container in a release (encrypted with the map's own content key, like the map's other bundles)
    // or, in a development install, the plain pack itself. Which thread does what:
    //   GAME THREAD (GrassPreparation.Start, called from the tick): opens the file, looks at its first four bytes, and for a container asks the resolver for the map's key
    //     (BundleKeyResolver.TryGetKey: derived once per map and session and cached, the same key the map's own bundles already needed; never logged here), takes a COPY of it and chooses the
    //     crypto provider. A missing file, an unreadable one or an unavailable key completes the preparation at once with a reason code.
    //   WORKER (a LongRunning task like the crowd's; GrassPreparation.Load): opens the decrypting stream (the copy of the key is cleared right after), reads the pack header and checks it
    //     (NativeGrassPack), reads the placement payload and parses it (NativeGrassPlacement), reads the three art payloads and checks their structure (NativeGrassArt.CheckBlocks), and
    //     disposes the stream. It never touches a Unity type, the plugin, the resolver or the log: a failure travels as a reason CODE in GrassInputs.Error, never a path or file content.
    //   GAME THREAD again (the tick polls IsCompleted, the glue builds from Result): the buffers go to the GPU and are dropped (GrassInputs.DropArt).
    // The bytes live in memory only: nothing is written, cached or logged. Files open read-only with sharing that does not block anything (FileShare.ReadWrite | Delete).

    /// <summary>Where the art bytes come from (developer key GrassArt).</summary>
    internal enum GrassArtKind
    {
        /// <summary>The three art payloads of the map's grass pack.</summary>
        Pack = 0,
        /// <summary>The BSG-free placeholder art of <see cref="NativeGrassArt.Placeholder"/> in the shapes the pack states (no art byte is read; the placement still is).</summary>
        Placeholder = 1,
    }

    /// <summary>The file layer of the grass pack (an injected fake in the tests).</summary>
    internal interface IGrassPackFiles
    {
        /// <summary>Opens the file for reading as a seekable stream, or returns null when it does not exist. Other failures throw IOException or UnauthorizedAccessException.</summary>
        Stream OpenRead(string path);
    }

    /// <summary>The real file layer: read-only, shared with everything.</summary>
    internal sealed class DiskGrassPackFiles : IGrassPackFiles
    {
        internal static readonly DiskGrassPackFiles Instance = new DiskGrassPackFiles();

        public Stream OpenRead(string path)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }
    }

    /// <summary>The content key of a map (BundleKeyResolver.TryGetKey in the plugin): false when it cannot be recovered. The returned array is shared; the preparation copies it.</summary>
    internal delegate bool GrassKeyResolver(string mapKey, out byte[] key);

    /// <summary>
    /// What the worker hands to the main thread: the parsed placement plan and the three raw block buffers, or a reason code. Plain data, no Unity type. <see cref="DropArt"/> releases
    /// the art buffers (the glue calls it right after the upload); the plan stays until the preparation is dropped.
    /// </summary>
    internal sealed class GrassInputs
    {
        /// <summary>null = ready; otherwise a code such as "pack-missing", "key-unavailable", "container:WrongMap", "pack-format:MapKeyMismatch", "placement:NotAffine@12", "art-blocks:GrassTexArray:MipChainIncoherent", "io:IOException".</summary>
        internal string Error;
        internal GrassPlan Plan;
        internal GrassPackLayout Layout;
        internal byte[] NormalTexture;
        internal byte[] TexArray;
        internal byte[] NormalArray;
        internal string ArtName;
        /// <summary>"AMPB" for an encrypted container, "plain" for a development pack (codes only, for the Debug line).</summary>
        internal string Container;
        /// <summary>Codes only, for the Debug line: "pack=ok placement=ok blocks=3/3".</summary>
        internal string Checks;
        internal long OpenTicks;
        internal long PlacementTicks;
        internal long ArtTicks;
        /// <summary>The plaintext bytes read from the pack (header, placement and, for the pack art, the three art payloads).</summary>
        internal long BytesRead;

        internal bool Ok => Error == null;

        internal int PlacementLength => Layout != null ? (int)Layout.PlacementSize : 0;

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
    /// error). <see cref="Abandon"/> asks the worker to stop between two reads.
    /// </summary>
    internal sealed class GrassPreparation
    {
        readonly Task<GrassInputs> _task;
        volatile bool _abandoned;

        GrassPreparation(Func<GrassPreparation, GrassInputs> work)
        {
            _task = Task.Factory.StartNew(() => work(this), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        GrassPreparation(GrassInputs done)
        {
            _task = Task.FromResult(done);
        }

        /// <summary>
        /// Starts the preparation of <paramref name="mapKey"/>'s grass from the pack at <paramref name="packPath"/>. Runs on the GAME THREAD (see the file comment): it opens the file, tells a container from
        /// a plain pack, and for a container resolves the key (<paramref name="resolveKey"/>) and the provider (<paramref name="provider"/>, called only then). The worker does the rest. Never throws.
        /// </summary>
        internal static GrassPreparation Start(string mapKey, string packPath, IGrassPackFiles files, GrassKeyResolver resolveKey, Func<ICryptoProvider> provider, GrassArtKind kind)
        {
            Stream file = null;
            byte[] key = null;
            try
            {
                file = files.OpenRead(packPath);
                if (file == null)
                {
                    return Done("pack-missing");
                }
                bool container = Ampb.IsAmpb(file);
                ICryptoProvider crypto = null;
                if (container)
                {
                    byte[] shared;
                    if (resolveKey == null || !resolveKey(mapKey, out shared) || shared == null || shared.Length == 0)
                    {
                        file.Dispose();
                        return Done("key-unavailable");
                    }
                    key = (byte[])shared.Clone();
                    crypto = provider != null ? provider() : null;
                }
                Stream opened = file;
                byte[] keyCopy = key;
                file = null;
                key = null; // the worker owns both now
                return new GrassPreparation(self => Load(self, mapKey, opened, container, keyCopy, crypto, kind));
            }
            catch (Exception e)
            {
                try { file?.Dispose(); } catch { }
                Clear(key);
                // the code carries the exception TYPE only: a message can hold a path of the player's disk
                return Done("pack-unreadable:" + e.GetType().Name);
            }
        }

        /// <summary>A preparation that is already finished with a reason code.</summary>
        static GrassPreparation Done(string error) => new GrassPreparation(new GrassInputs { Error = error });

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

        static void Clear(byte[] key)
        {
            if (key != null)
            {
                Array.Clear(key, 0, key.Length);
            }
        }

        /// <summary>
        /// The worker's whole load, synchronously: also what the tests call. <paramref name="file"/> is the open pack file (this method disposes it), <paramref name="container"/> says whether it is an AMPB
        /// container (then <paramref name="key"/>, a copy this method clears, and <paramref name="provider"/> open it). Never throws.
        /// </summary>
        internal static GrassInputs Load(GrassPreparation owner, string mapKey, Stream file, bool container, byte[] key, ICryptoProvider provider, GrassArtKind kind)
        {
            var inputs = new GrassInputs();
            Stream pack = file;
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                if (container)
                {
                    inputs.Container = "AMPB";
                    try
                    {
                        // the constructor authenticates the header with the key (a wrong key or a modified header fails here) and disposes the file when it fails; it keeps no copy of the key
                        pack = new AmpbReadStream(file, mapKey, key, provider, 4, false);
                    }
                    finally
                    {
                        Clear(key);
                    }
                }
                else
                {
                    inputs.Container = "plain";
                }

                var header = new byte[NativeGrassPack.HeaderSize];
                int got = ReadUpTo(pack, 0, header, header.Length);
                inputs.BytesRead += got;
                GrassPackLayout layout;
                GrassPackError packError = NativeGrassPack.Parse(header, got, pack.Length, mapKey, out layout);
                if (packError != GrassPackError.None)
                {
                    inputs.Error = "pack-format:" + packError;
                    return inputs;
                }
                inputs.Layout = layout;
                inputs.OpenTicks = Stopwatch.GetTimestamp() - t0;
                if (Gone(owner, inputs)) return inputs;

                // the placement: the payload is Arena's .pcl file byte for byte
                long t1 = Stopwatch.GetTimestamp();
                var records = new byte[(int)layout.PlacementSize];
                if (!ReadExact(pack, layout.PlacementOffset, records))
                {
                    inputs.Error = "placement:ShortRead";
                    return inputs;
                }
                inputs.BytesRead += records.Length;
                GrassParseResult parsed = NativeGrassPlacement.Parse(records, records.Length, layout.SliceCount);
                if (!parsed.Ok)
                {
                    inputs.Error = "placement:" + parsed.Error + (parsed.ErrorIndex >= 0 ? "@" + parsed.ErrorIndex.ToString(CultureInfo.InvariantCulture) : string.Empty);
                    return inputs;
                }
                inputs.Plan = parsed.Plan;
                inputs.PlacementTicks = Stopwatch.GetTimestamp() - t1;
                if (Gone(owner, inputs)) return inputs;

                // the art: the three payloads (or generated blocks of the same shapes), the structure check for the pack's own bytes
                long t2 = Stopwatch.GetTimestamp();
                inputs.ArtName = kind == GrassArtKind.Placeholder ? "Placeholder" : "Pack";
                int blocksOk = 0;
                foreach (GrassArtRange range in layout.Ranges)
                {
                    if (Gone(owner, inputs)) return inputs;
                    var data = new byte[(int)range.Size];
                    if (kind == GrassArtKind.Placeholder)
                    {
                        NativeGrassArt.Placeholder.Fill(data, range);
                    }
                    else
                    {
                        if (!ReadExact(pack, range.Offset, data))
                        {
                            inputs.Error = "art-read:" + range.Name + ":" + GrassArtError.ShortRead;
                            return inputs;
                        }
                        inputs.BytesRead += data.Length;
                        // the structure check is for the pack's bytes: the generated placeholder is correct by construction and has no coherent mip chain to test
                        GrassArtError error = NativeGrassArt.CheckBlocks(data, range);
                        if (error != GrassArtError.None)
                        {
                            inputs.Error = "art-blocks:" + range.Name + ":" + error;
                            return inputs;
                        }
                        blocksOk++;
                    }
                    if (ReferenceEquals(range, layout.NormalTexture)) inputs.NormalTexture = data;
                    else if (ReferenceEquals(range, layout.TexArray)) inputs.TexArray = data;
                    else inputs.NormalArray = data;
                }
                inputs.ArtTicks = Stopwatch.GetTimestamp() - t2;
                inputs.Checks = kind == GrassArtKind.Placeholder
                    ? "pack=ok placement=ok art=generated"
                    : "pack=ok placement=ok blocks=" + blocksOk.ToString(CultureInfo.InvariantCulture) + "/" + layout.Ranges.Length.ToString(CultureInfo.InvariantCulture);
                return inputs;
            }
            catch (AmpbException e)
            {
                inputs.Error = "container:" + e.Kind;
                inputs.DropArt();
                return inputs;
            }
            catch (Exception e)
            {
                // the code carries the exception TYPE only: a message can hold a path of the player's disk
                inputs.Error = "io:" + e.GetType().Name;
                inputs.DropArt();
                return inputs;
            }
            finally
            {
                Clear(key);
                try { pack?.Dispose(); } catch { }
                if (!ReferenceEquals(pack, file))
                {
                    try { file?.Dispose(); } catch { }
                }
            }
        }

        static bool Gone(GrassPreparation owner, GrassInputs inputs)
        {
            if (owner != null && owner.Abandoned)
            {
                inputs.Error = "worker:abandoned";
                return true;
            }
            return false;
        }

        static int ReadUpTo(Stream s, long position, byte[] buffer, int count)
        {
            s.Seek(position, SeekOrigin.Begin);
            int done = 0;
            while (done < count)
            {
                int n = s.Read(buffer, done, count - done);
                if (n <= 0)
                {
                    break;
                }
                done += n;
            }
            return done;
        }

        static bool ReadExact(Stream s, long position, byte[] buffer)
        {
            return ReadUpTo(s, position, buffer, buffer.Length) == buffer.Length;
        }
    }
}
