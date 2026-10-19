using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArenaMapPort.Crypto;
using ArenaMapPort.Shared;

namespace ArenaMapPort.BundleCrypt
{
    /// <summary>Where the Arena level files come from for one command: the folder and the launcher manifest version that goes into the key file's built_for.</summary>
    internal sealed class ArenaInfo
    {
        public string InstallPath;
        public string Version;
        public string Description;
    }

    /// <summary>
    /// The commands of the tool (docs/PLAN-ENCRYPTION.md TOOL-1). Every command is one static method that prints one "OK ..." line on success and throws
    /// <see cref="ToolFailure"/> / <see cref="UsageException"/> / <see cref="AmpbException"/> otherwise (Program turns them into exit codes). All crypto goes
    /// through the ArenaMapPort.Crypto sources the client plugin links, so what this tool writes is read by the same code the player runs.
    /// Proved by tools/tests/test_bundle_crypt_tool.py (the executable on synthetic bundles and a synthetic Arena folder) and by the staging run of the client build.
    /// </summary>
    internal static class Commands
    {
        public const string UsageText =
@"ArenaMapPort.BundleCrypt - encrypts the map bundles and manages their keys (docs/PLAN-ENCRYPTION.md)

  encrypt      --map-key K --in PLAIN --out ENC --key-store DIR [--create-key] [--chunk-size N]
  decrypt      --in ENC --out PLAIN  KEYSOURCE
  inspect      --in ENC [--verify-chunks] [--json] [KEYSOURCE]
  read-range   --in ENC --offset N --length L [--out FILE|-] [--clip]  KEYSOURCE   (N < 0 counts from the plaintext end; stdout by default)
  plain-sha256 --in ENC  KEYSOURCE
  keys         --map-key K --key-store DIR (--levels FILE | --manifest FILE) --out KEYFILE [--create-key]  ARENA
  verify-keys  --map-key K --keys-file KEYFILE --key-store DIR  ARENA     (the player's derivation must return the store's key)
  verify       --map-key K --plain PLAIN --enc ENC --key-store DIR        (ENC must be exactly the encryption of PLAIN)
  stage        --map-key K --bundle PLAIN [--bundle PLAIN ...] --out-dir PLUGINDIR --key-store DIR (--levels FILE | --manifest FILE) [--create-key]  ARENA
               (encrypts into PLUGINDIR/bundles and writes PLUGINDIR/keys/K.json)
  rotate-key   --map-key K --key-store DIR --yes
  bench        [--provider cng|managed] [--size-mib N]

  The container may be given as the one positional argument of inspect, read-range, plain-sha256 and decrypt instead of --in.
  KEYSOURCE = --key-store DIR  |  --keys-file FILE [ARENA]      ARENA = --arena DIR|auto  (default auto: the BSG launcher's registry entry)
  --provider cng|managed|auto selects the crypto implementation (default auto: Windows CNG when it passes its self-test).
  Exit 0 = success (one OK line), 1 = failure (one FAIL line on stderr), 2 = usage. Keys are never printed.
  Test seam: --arena-unverified DIR [--built-for TEXT] skips the installation check and needs ARENAMAPPORT_TOOL_TEST=1.";

        public static int Run(string command, Args a)
        {
            bool takesFile = command == "inspect" || command == "read-range" || command == "plain-sha256" || command == "decrypt";
            int allowed = takesFile ? 1 : 0;
            if (a.Positional.Count > allowed)
                throw new UsageException("unexpected argument '" + a.Positional[allowed] + "'");
            switch (command)
            {
                case "encrypt": return Encrypt(a);
                case "decrypt": return Decrypt(a);
                case "inspect": return Inspect(a);
                case "read-range": return ReadRange(a);
                case "plain-sha256": return PlainSha256(a);
                case "keys": return Keys(a);
                case "verify-keys": return VerifyKeys(a);
                case "verify": return Verify(a);
                case "stage": return Stage(a);
                case "rotate-key": return RotateKey(a);
                case "bench": return Bench(a);
                default: throw new UsageException("unknown command '" + command + "'");
            }
        }

        // ---- shared pieces ------------------------------------------------------------------------------------------

        private static string MapKeyOf(Args a)
        {
            string key = a.Require("map-key");
            if (!Ampb.IsValidMapKey(key))
                throw new UsageException("--map-key must be 1.." + Ampb.MaxMapKeyLength + " characters of [a-z0-9_]");
            return key;
        }

        private static ICryptoProvider GetProvider(Args a)
        {
            string choice = a.Get("provider") ?? "auto";
            switch (choice)
            {
                case "managed":
                    return CryptoProviders.Managed;
                case "cng":
                    ICryptoProvider cng = CryptoProviders.Cng;
                    if (cng == null)
                        throw new ToolFailure("Windows CNG was requested but is unavailable (" + CryptoProviders.CngUnavailableReason + ")");
                    return cng;
                case "auto":
                    return CryptoProviders.Select(delegate (string warning) { Console.Error.WriteLine("WARN " + warning); });
                default:
                    throw new UsageException("--provider must be cng, managed or auto");
            }
        }

        private static ArenaInfo LocateArena(Args a)
        {
            string unverified = a.Get("arena-unverified");
            if (unverified != null)
            {
                if (a.Has("arena"))
                    throw new UsageException("--arena-unverified and --arena exclude each other");
                if (Environment.GetEnvironmentVariable("ARENAMAPPORT_TOOL_TEST") != "1")
                    throw new UsageException("--arena-unverified is a test seam and needs ARENAMAPPORT_TOOL_TEST=1; the staging build and the validators never use it");
                ArenaInfo fake = new ArenaInfo();
                fake.InstallPath = Path.GetFullPath(unverified);
                fake.Version = a.Get("built-for") ?? "test-1";
                fake.Description = "unverified test folder " + fake.InstallPath;
                return fake;
            }
            if (a.Has("built-for"))
                throw new UsageException("--built-for belongs to --arena-unverified");
            string arena = a.Get("arena") ?? "auto";
            ArenaInstallationResult result = arena == "auto" ? ArenaInstallation.Detect(null) : ArenaInstallation.Verify(arena);
            if (!result.Verified)
                throw new ToolFailure("the Arena installation is not verified: " + result.Reason);
            ArenaInfo info = new ArenaInfo();
            info.InstallPath = result.InstallPath;
            info.Version = string.IsNullOrEmpty(result.ManifestVersion) ? "unknown" : result.ManifestVersion;
            info.Description = "Arena " + info.Version + " at " + info.InstallPath;
            return info;
        }

        private static string DescribeFailure(KeyResolution r)
        {
            return r.Cause + (string.IsNullOrEmpty(r.Detail) ? "" : ": " + r.Detail);
        }

        /// <summary>The content key for a container: from the key store, or derived from the Arena files through a shipped key file (the player's path).</summary>
        private static byte[] ResolveKMap(Args a, ICryptoProvider p, string containerMapKey)
        {
            string storeDir = a.Get("key-store");
            string keysFile = a.Get("keys-file");
            if (storeDir != null && keysFile != null)
                throw new UsageException("give --key-store or --keys-file, not both");
            if (storeDir != null)
                return KeyStore.Open(storeDir, containerMapKey, false).KMap;
            if (keysFile != null)
            {
                ArenaInfo arena = LocateArena(a);
                KeyResolution r = KeyResolver.ResolveFile(keysFile, arena.InstallPath, arena.Version, new DirectoryArenaFiles(arena.InstallPath), p, containerMapKey);
                if (!r.Ok)
                    throw new ToolFailure("key derivation failed (" + DescribeFailure(r) + ")");
                return r.MapKey;
            }
            throw new UsageException("a key source is required: --key-store DIR, or --keys-file FILE [--arena DIR|auto]");
        }

        /// <summary>The container of inspect, read-range, plain-sha256 and decrypt: --in FILE, or the one positional argument.</summary>
        private static string ContainerOf(Args a)
        {
            string named = a.Get("in");
            if (named != null && a.Positional.Count > 0)
                throw new UsageException("give the file as --in or as the positional argument, not both");
            string path = named ?? (a.Positional.Count == 1 ? a.Positional[0] : null);
            if (path == null)
                throw new UsageException("--in FILE is required");
            return path;
        }

        private static bool HasKeySource(Args a)
        {
            return a.Has("key-store") || a.Has("keys-file");
        }

        /// <summary>Structure-checks a container (no key) and returns its header; anything that is not a valid container is a failure naming the file.</summary>
        private static AmpbHeader StructureOf(string path)
        {
            AmpbInfo info;
            using (FileStream fs = Util.OpenRead(path))
                info = Ampb.Inspect(fs);
            if (!info.IsAmpb)
                throw new ToolFailure("'" + Path.GetFileName(path) + "' is not an AMPB container (it starts with '" + Util.FirstBytesText(path, 8) + "')");
            if (info.Error != null)
                throw new ToolFailure(Path.GetFileName(path) + ": " + info.Error.Kind + ": " + info.Error.Message);
            return info.Header;
        }

        private static AmpbReadStream OpenPlain(string path, Args a, ICryptoProvider p, int cacheSlots)
        {
            AmpbHeader structure = StructureOf(path);
            byte[] kMap = ResolveKMap(a, p, structure.MapKey);
            return Ampb.Open(path, kMap, p, null, cacheSlots);
        }

        // ---- encrypt ------------------------------------------------------------------------------------------------

        private sealed class EncryptOutcome
        {
            public bool Written;
            public long PlainLength;
            public long FileLength;
            public int Chunks;
            public double Ms;
        }

        /// <summary>
        /// Encrypts one bundle to <paramref name="outPath"/>. An existing output that is already exactly the right bytes is left alone (compared by a dry
        /// encryption pass, no copy): a rebuild of an unchanged bundle touches nothing, and the file keeps its time. Otherwise the container is written
        /// to a temporary file next to the output and renamed over it.
        /// </summary>
        private static EncryptOutcome EncryptFile(string mapKey, string inPath, string outPath, byte[] kMap, ICryptoProvider p, int chunkSize)
        {
            long started = Stopwatch.GetTimestamp();
            if (Util.StartsWithAmpb(inPath))
                throw new ToolFailure("'" + inPath + "' is already an AMPB container; the input must be the plaintext bundle");
            if (string.Equals(Path.GetFullPath(inPath), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
                throw new UsageException("--in and --out are the same file");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            EncryptOutcome outcome = new EncryptOutcome();
            using (FileStream plain = new FileStream(inPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            {
                outcome.PlainLength = plain.Length;
                if (outcome.PlainLength == 0)
                    throw new ToolFailure("'" + inPath + "' is empty: there is nothing to encrypt");
                bool same = false;
                AmpbHeader header = null;
                // Only an existing AMPB container is worth a dry encryption pass; a plaintext file of the same name (the old copy step) is simply replaced.
                if (File.Exists(outPath) && Util.StartsWithAmpb(outPath))
                {
                    using (FileStream existing = new FileStream(outPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                    {
                        CompareStream compare = new CompareStream(existing);
                        try
                        {
                            header = Ampb.Encrypt(plain, compare, mapKey, kMap, p, chunkSize);
                            same = true;
                        }
                        catch (CompareMismatchException)
                        {
                            same = false;
                        }
                    }
                    plain.Position = 0;
                }
                if (!same)
                {
                    string tmp = outPath + ".tmp";
                    try
                    {
                        using (FileStream dest = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16))
                            header = Ampb.Encrypt(plain, dest, mapKey, kMap, p, chunkSize);
                        File.Move(tmp, outPath, true);
                    }
                    catch (Exception)
                    {
                        try { File.Delete(tmp); } catch (IOException) { }
                        throw;
                    }
                }
                outcome.Written = !same;
                outcome.FileLength = header.FileLength;
                outcome.Chunks = header.ChunkCount;
            }
            outcome.Ms = Util.Ms(started);
            return outcome;
        }

        private static string EncryptLine(string name, EncryptOutcome o, ICryptoProvider p)
        {
            return name + ": " + Util.N(o.PlainLength) + " B plaintext, " + Util.N(o.FileLength) + " B encrypted, " + o.Chunks + " chunks, "
                + (o.Written ? "written" : "unchanged") + ", " + Math.Round(o.Ms).ToString(CultureInfo.InvariantCulture) + " ms, crypto: " + p.Name;
        }

        private static int ChunkSizeOf(Args a)
        {
            if (!a.Has("chunk-size"))
                return Ampb.DefaultChunkSize;
            long size = a.GetLong("chunk-size");
            if (size < Ampb.MinChunkSize || size > Ampb.MaxChunkSize || (size & 15) != 0)
                throw new UsageException("--chunk-size must be a multiple of 16 between " + Ampb.MinChunkSize + " and " + Ampb.MaxChunkSize);
            return (int)size;
        }

        private static void AnnounceCreated(KeyStoreEntry store)
        {
            if (store.JustCreated)
                Console.Out.WriteLine("CREATED " + store.Path + " (the content key of " + store.MapKey
                    + "): BACK UP the key store folder now and never commit or ship it; without it no key file can be made for the bundles encrypted with it");
        }

        private static int Encrypt(Args a)
        {
            a.Check("in", "out", "map-key", "key-store", "create-key", "chunk-size", "provider");
            string mapKey = MapKeyOf(a);
            string inPath = a.Require("in"), outPath = a.Require("out");
            ICryptoProvider p = GetProvider(a);
            if (!File.Exists(inPath))
                throw new ToolFailure("file not found: " + inPath);
            KeyStoreEntry store = KeyStore.Open(a.Require("key-store"), mapKey, a.Flag("create-key"));
            AnnounceCreated(store);
            EncryptOutcome o = EncryptFile(mapKey, inPath, outPath, store.KMap, p, ChunkSizeOf(a));
            Console.Out.WriteLine("OK encrypt " + mapKey + " " + EncryptLine(Path.GetFileName(outPath), o, p));
            return 0;
        }

        // ---- decrypt, inspect, read-range, plain-sha256 ---------------------------------------------------------------

        private static int Decrypt(Args a)
        {
            a.Check("in", "out", "key-store", "keys-file", "arena", "arena-unverified", "built-for", "provider");
            string inPath = ContainerOf(a), outPath = a.Require("out");
            ICryptoProvider p = GetProvider(a);
            AmpbHeader structure = StructureOf(inPath);
            if (string.Equals(Path.GetFullPath(inPath), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase))
                throw new UsageException("--in and --out are the same file");
            byte[] kMap = ResolveKMap(a, p, structure.MapKey);
            long started = Stopwatch.GetTimestamp();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            string tmp = outPath + ".tmp";
            long total;
            try
            {
                using (FileStream container = Util.OpenRead(inPath))
                using (FileStream dest = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    total = Ampb.Decrypt(container, dest, kMap, p, structure.MapKey);
                File.Move(tmp, outPath, true);
            }
            catch (Exception)
            {
                try { File.Delete(tmp); } catch (IOException) { }
                throw;
            }
            Console.Out.WriteLine("OK decrypt " + structure.MapKey + " " + Path.GetFileName(inPath) + " -> " + outPath + ": " + Util.N(total) + " B in "
                + Math.Round(Util.Ms(started)).ToString(CultureInfo.InvariantCulture) + " ms, crypto: " + p.Name);
            return 0;
        }

        private static int Inspect(Args a)
        {
            a.Check("in", "verify-chunks", "json", "key-store", "keys-file", "arena", "arena-unverified", "built-for", "provider");
            string path = ContainerOf(a);
            AmpbHeader h = StructureOf(path);
            ICryptoProvider p = GetProvider(a);
            bool? macVerified = null;
            string chunks = null;
            if (HasKeySource(a))
            {
                byte[] kMap = ResolveKMap(a, p, h.MapKey);
                using (FileStream fs = Util.OpenRead(path))
                {
                    AmpbHeader authenticated = Ampb.ReadHeader(fs, kMap, p, null);
                    macVerified = true;
                    if (a.Flag("verify-chunks"))
                    {
                        Ampb.VerifyChunks(fs, authenticated, p);
                        chunks = "verified";
                    }
                }
            }
            else if (a.Flag("verify-chunks"))
            {
                using (FileStream fs = Util.OpenRead(path))
                {
                    // Without the key the table itself cannot be trusted; the comparison still finds a damaged file.
                    Ampb.VerifyChunks(fs, ParseUnauthenticated(fs), p);
                    chunks = "consistent-with-unauthenticated-table";
                }
            }

            string name = Path.GetFileName(path);
            if (a.Flag("json"))
            {
                using (MemoryStream json = new MemoryStream())
                {
                    using (Utf8JsonWriter w = new Utf8JsonWriter(json))
                    {
                        w.WriteStartObject();
                        w.WriteString("file", name);
                        w.WriteBoolean("ampb", true);
                        w.WriteNumber("version", h.Version);
                        w.WriteNumber("flags", h.Flags);
                        w.WriteString("map_key", h.MapKey);
                        w.WriteNumber("chunk_size", h.ChunkSize);
                        w.WriteNumber("chunk_count", h.ChunkCount);
                        w.WriteNumber("plaintext_length", h.PlaintextLength);
                        w.WriteNumber("table_offset", h.TableOffset);
                        w.WriteNumber("payload_offset", h.PayloadOffset);
                        w.WriteNumber("file_length", h.FileLength);
                        w.WriteString("nonce", KeyStore.ToHex(h.Nonce));
                        if (macVerified.HasValue) w.WriteBoolean("mac_verified", macVerified.Value); else w.WriteNull("mac_verified");
                        if (chunks != null) w.WriteString("chunks", chunks); else w.WriteNull("chunks");
                        w.WriteEndObject();
                    }
                    Console.Out.WriteLine(Encoding.UTF8.GetString(json.ToArray()));
                }
                return 0;
            }
            Console.Out.WriteLine("  file            " + name);
            Console.Out.WriteLine("  magic           AMPB, version " + h.Version + ", flags " + h.Flags);
            Console.Out.WriteLine("  map key         " + h.MapKey);
            Console.Out.WriteLine("  chunks          " + h.ChunkCount + " x " + Util.N(h.ChunkSize) + " B");
            Console.Out.WriteLine("  plaintext       " + Util.N(h.PlaintextLength) + " B");
            Console.Out.WriteLine("  file length     " + Util.N(h.FileLength) + " B (header and chunk table " + Util.N(h.PayloadOffset) + " B)");
            Console.Out.WriteLine("  nonce           " + KeyStore.ToHex(h.Nonce));
            Console.Out.WriteLine("  header MAC      " + (macVerified.HasValue ? "verified" : "not checked (no key source)"));
            Console.Out.WriteLine("  chunk hashes    " + (chunks ?? "not checked (--verify-chunks)"));
            Console.Out.WriteLine("OK inspect " + name + ": AMPB v" + h.Version + " " + h.MapKey + ", " + Util.N(h.PlaintextLength) + " B plaintext in " + h.ChunkCount
                + " chunks, mac " + (macVerified.HasValue ? "verified" : "unchecked") + ", chunks " + (chunks ?? "unchecked"));
            return 0;
        }

        /// <summary>The header and table without a MAC check, for the key-less chunk comparison of inspect.</summary>
        private static AmpbHeader ParseUnauthenticated(FileStream fs)
        {
            AmpbInfo info = Ampb.Inspect(fs);
            if (info.Error != null)
                throw info.Error;
            return info.Header;
        }

        private static int ReadRange(Args a)
        {
            a.Check("in", "offset", "length", "out", "clip", "key-store", "keys-file", "arena", "arena-unverified", "built-for", "provider");
            string path = ContainerOf(a);
            long offset = a.GetLong("offset"), length = a.GetLong("length");
            if (length < 0)
                throw new UsageException("--length must not be negative");
            string outTarget = a.Get("out") ?? "-";
            ICryptoProvider p = GetProvider(a);
            using (AmpbReadStream s = OpenPlain(path, a, p, 2))
            {
                long total = s.Length;
                if (offset < 0)
                    offset = total + offset;
                if (offset < 0 || offset > total)
                    throw new ToolFailure("the offset is outside the plaintext (" + Util.N(total) + " B)");
                if (offset + length > total)
                {
                    if (!a.Flag("clip"))
                        throw new ToolFailure("the range " + Util.N(offset) + "+" + Util.N(length) + " reaches past the end of the plaintext (" + Util.N(total) + " B); --clip shortens it");
                    length = total - offset;
                }
                bool toStdout = outTarget == "-";
                Stream sink = toStdout ? Console.OpenStandardOutput() : new FileStream(outTarget, FileMode.Create, FileAccess.Write, FileShare.None);
                try
                {
                    byte[] buffer = new byte[(int)Math.Min(1 << 20, Math.Max(length, 1))];
                    long position = offset, left = length;
                    while (left > 0)
                    {
                        int want = (int)Math.Min(buffer.Length, left);
                        int got = s.ReadAt(position, buffer, 0, want);
                        if (got <= 0)
                            throw new ToolFailure("the plaintext ended early at offset " + Util.N(position));
                        sink.Write(buffer, 0, got);
                        position += got;
                        left -= got;
                    }
                    sink.Flush();
                }
                finally
                {
                    if (!toStdout)
                        sink.Dispose();
                }
                string line = "OK read-range " + Path.GetFileName(path) + ": " + Util.N(length) + " B at " + Util.N(offset) + " of " + Util.N(total);
                (toStdout ? Console.Error : Console.Out).WriteLine(line);
            }
            return 0;
        }

        private static int PlainSha256(Args a)
        {
            a.Check("in", "key-store", "keys-file", "arena", "arena-unverified", "built-for", "provider");
            string path = ContainerOf(a);
            ICryptoProvider p = GetProvider(a);
            long started = Stopwatch.GetTimestamp();
            using (AmpbReadStream s = OpenPlain(path, a, p, 2))
            using (ISha256Stream hash = p.NewSha256())
            {
                byte[] buffer = new byte[Ampb.DefaultChunkSize];
                long total = 0;
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.Update(buffer, 0, n);
                    total += n;
                }
                Console.Out.WriteLine("OK plain-sha256 " + KeyStore.ToHex(hash.Finish()) + " " + total + " B " + Path.GetFileName(path) + " in "
                    + Math.Round(Util.Ms(started)).ToString(CultureInfo.InvariantCulture) + " ms");
            }
            return 0;
        }

        // ---- keys, verify-keys, stage -----------------------------------------------------------------------------------

        private static KeyWrap BuildWrap(string mapKey, KeyStoreEntry store, ArenaInfo arena, List<int> levels, ICryptoProvider p, out string[] hashHex, out long bytesHashed)
        {
            DirectoryArenaFiles files = new DirectoryArenaFiles(arena.InstallPath);
            List<KeyFileEntry> entries = new List<KeyFileEntry>();
            byte[][] hashes = new byte[levels.Count][];
            hashHex = new string[levels.Count];
            bytesHashed = 0;
            for (int i = 0; i < levels.Count; i++)
            {
                string rel = LevelSources.RelativePath(levels[i]);
                long size;
                try
                {
                    size = files.Length(rel);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException))
                        throw;
                    throw new ToolFailure("level file '" + rel + "' cannot be read in " + arena.InstallPath + " (" + ex.Message + ")");
                }
                if (size < 0)
                    throw new ToolFailure("level file '" + rel + "' is missing in " + arena.InstallPath
                        + " (Arena's level indices may have shifted: regenerate tools/bundle/key-sources.json)");
                long hashed;
                try
                {
                    using (Stream s = files.Open(rel))
                        hashes[i] = KeyResolver.HashStream(s, p, out hashed);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException))
                        throw;
                    throw new ToolFailure("level file '" + rel + "' cannot be read in " + arena.InstallPath + " (" + ex.Message + ")");
                }
                if (hashed != size)
                    throw new ToolFailure("level file '" + rel + "' changed while it was hashed (" + hashed + " against " + size + " bytes)");
                entries.Add(new KeyFileEntry(rel, size));
                hashHex[i] = KeyStore.ToHex(hashes[i]);
                bytesHashed += size;
            }
            return KeyResolver.Wrap(mapKey, store.KMap, arena.Version, entries, hashes, p);
        }

        private sealed class KeysOutcome
        {
            public bool WrapAdded;
            public bool FileWritten;
            public int WrapCount;
            public int FileCount;
            public long BytesHashed;
            public double Ms;
            public int WrapIndex;
        }

        /// <summary>
        /// Adds the wrap for the installed Arena to the store (when it is new), writes the shipped key file (canonical bytes, left alone when unchanged) and then
        /// resolves the written file against the same Arena folder exactly as a player's client does: the unwrapped key must equal the store's content key.
        /// </summary>
        private static KeysOutcome WriteKeyFile(string mapKey, KeyStoreEntry store, ArenaInfo arena, List<int> levels, ICryptoProvider p, string outFile)
        {
            long started = Stopwatch.GetTimestamp();
            string[] hashHex;
            long bytesHashed;
            KeyWrap wrap = BuildWrap(mapKey, store, arena, levels, p, out hashHex, out bytesHashed);
            KeysOutcome o = new KeysOutcome();
            o.WrapAdded = KeyStore.AddWrap(store, wrap, hashHex);
            if (o.WrapAdded)
                KeyStore.Save(store);
            KeyFile file = KeyStore.ToKeyFile(store);
            byte[] bytes = file.ToCanonicalBytes();
            o.FileWritten = Util.WriteIfChanged(outFile, bytes);
            KeyResolution r = KeyResolver.Resolve(KeyFile.Parse(bytes), arena.InstallPath, arena.Version, new DirectoryArenaFiles(arena.InstallPath), p, mapKey);
            if (!r.Ok || !ConstantTime.AreEqual(r.MapKey, store.KMap))
                throw new ToolFailure("self-check failed: the key file just written does not unlock the content key against " + arena.Description
                    + (r.Ok ? "" : " (" + DescribeFailure(r) + ")"));
            o.WrapCount = store.Wraps.Count;
            o.FileCount = levels.Count;
            o.BytesHashed = bytesHashed;
            o.WrapIndex = r.WrapIndex;
            o.Ms = Util.Ms(started);
            return o;
        }

        private static string KeysLine(ArenaInfo arena, KeysOutcome o, ICryptoProvider p)
        {
            return (o.WrapAdded ? "new wrap for " : "wrap present for ") + "Arena " + arena.Version + ", " + o.WrapCount + " wrap(s) in the file ("
                + (o.FileWritten ? "written" : "unchanged") + "), " + o.FileCount + " level files " + Util.N(o.BytesHashed) + " B hashed, self-check unlocks with wrap "
                + o.WrapIndex + ", " + Math.Round(o.Ms).ToString(CultureInfo.InvariantCulture) + " ms, crypto: " + p.Name;
        }

        private static int Keys(Args a)
        {
            a.Check("map-key", "key-store", "levels", "manifest", "out", "create-key", "arena", "arena-unverified", "built-for", "provider");
            string mapKey = MapKeyOf(a);
            ICryptoProvider p = GetProvider(a);
            List<int> levels = LevelSources.Indices(mapKey, a.Get("levels"), a.Get("manifest"));
            ArenaInfo arena = LocateArena(a);  // before the key store: a machine without a verified Arena must not leave a new key behind
            KeyStoreEntry store = KeyStore.Open(a.Require("key-store"), mapKey, a.Flag("create-key"));
            AnnounceCreated(store);
            KeysOutcome o = WriteKeyFile(mapKey, store, arena, levels, p, a.Require("out"));
            Console.Out.WriteLine("OK keys " + mapKey + ": " + KeysLine(arena, o, p));
            return 0;
        }

        private static int VerifyKeys(Args a)
        {
            a.Check("map-key", "keys-file", "key-store", "arena", "arena-unverified", "built-for", "provider");
            string mapKey = MapKeyOf(a);
            ICryptoProvider p = GetProvider(a);
            KeyStoreEntry store = KeyStore.Open(a.Require("key-store"), mapKey, false);
            ArenaInfo arena = LocateArena(a);
            string keysFile = a.Require("keys-file");
            KeyResolution r = KeyResolver.ResolveFile(keysFile, arena.InstallPath, arena.Version, new DirectoryArenaFiles(arena.InstallPath), p, mapKey);
            if (!r.Ok)
                throw new ToolFailure("verify-keys " + mapKey + ": the key file does not unlock against " + arena.Description + " (" + DescribeFailure(r) + ")");
            if (!ConstantTime.AreEqual(r.MapKey, store.KMap))
                throw new ToolFailure("verify-keys " + mapKey + ": the key file unlocks a key that is not the one in the key store (the key file belongs to another build or key store)");
            Console.Out.WriteLine("OK verify-keys " + mapKey + ": wrap " + r.WrapIndex + " (built for " + r.BuiltFor + ") unlocks the store's content key against " + arena.Description + " ("
                + r.FilesHashed + " files, " + Util.N(r.BytesHashed) + " B hashed, " + r.Ms + " ms, crypto: " + r.Provider + ")");
            return 0;
        }

        private static int Verify(Args a)
        {
            a.Check("map-key", "plain", "enc", "key-store", "chunk-size", "provider");
            string mapKey = MapKeyOf(a);
            string plainPath = a.Require("plain"), encPath = a.Require("enc");
            ICryptoProvider p = GetProvider(a);
            KeyStoreEntry store = KeyStore.Open(a.Require("key-store"), mapKey, false);
            long started = Stopwatch.GetTimestamp();
            if (!File.Exists(encPath))
                throw new ToolFailure("file not found: " + encPath);
            using (FileStream plain = Util.OpenRead(plainPath))
            using (FileStream existing = Util.OpenRead(encPath))
            {
                CompareStream compare = new CompareStream(existing);
                try
                {
                    Ampb.Encrypt(plain, compare, mapKey, store.KMap, p, ChunkSizeOf(a));
                }
                catch (CompareMismatchException ex)
                {
                    throw new ToolFailure("verify " + mapKey + ": " + Path.GetFileName(encPath) + " is not the encryption of " + Path.GetFileName(plainPath) + " (" + ex.Message + ")");
                }
                Console.Out.WriteLine("OK verify " + mapKey + ": " + Path.GetFileName(encPath) + " is exactly the encryption of " + Path.GetFileName(plainPath) + " (" + Util.N(plain.Length) + " B, "
                    + Math.Round(Util.Ms(started)).ToString(CultureInfo.InvariantCulture) + " ms, crypto: " + p.Name + ")");
            }
            return 0;
        }

        private static int Stage(Args a)
        {
            a.Check("map-key", "bundle", "out-dir", "key-store", "levels", "manifest", "create-key", "arena", "arena-unverified", "built-for", "provider", "chunk-size");
            string mapKey = MapKeyOf(a);
            IList<string> bundles = a.GetAll("bundle");
            if (bundles.Count == 0)
                throw new UsageException("--bundle PLAIN is required (once per bundle)");
            string outDir = a.Require("out-dir");
            Regex nameRule = new Regex("^" + Regex.Escape(mapKey) + "_(scenes|navmesh|preset)\\.bundle$", RegexOptions.CultureInvariant);
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string bundle in bundles)
            {
                string name = Path.GetFileName(bundle);
                if (!nameRule.IsMatch(name))
                    throw new ToolFailure("'" + name + "' is not a bundle of '" + mapKey + "' (expected " + mapKey + "_scenes|navmesh|preset.bundle)");
                if (!names.Add(name))
                    throw new UsageException("the bundle " + name + " is listed twice");
                if (!File.Exists(bundle))
                    throw new ToolFailure("file not found: " + bundle);
            }
            ICryptoProvider p = GetProvider(a);
            List<int> levels = LevelSources.Indices(mapKey, a.Get("levels"), a.Get("manifest"));
            ArenaInfo arena = LocateArena(a);  // before the key store: a machine without a verified Arena must not leave a new key behind
            KeyStoreEntry store = KeyStore.Open(a.Require("key-store"), mapKey, a.Flag("create-key"));
            AnnounceCreated(store);
            int chunkSize = ChunkSizeOf(a);
            int written = 0;
            foreach (string bundle in bundles)
            {
                string name = Path.GetFileName(bundle);
                string target = Path.Combine(outDir, "bundles", name);
                EncryptOutcome o = EncryptFile(mapKey, bundle, target, store.KMap, p, chunkSize);
                if (o.Written)
                    written++;
                Console.Out.WriteLine("  " + EncryptLine(name, o, p));
            }
            string keyPath = Path.Combine(outDir, "keys", mapKey + ".json");
            KeysOutcome k = WriteKeyFile(mapKey, store, arena, levels, p, keyPath);
            Console.Out.WriteLine("OK stage " + mapKey + ": " + bundles.Count + " bundles encrypted (" + written + " written), key file keys/" + mapKey + ".json: " + KeysLine(arena, k, p));
            return 0;
        }

        private static int RotateKey(Args a)
        {
            a.Check("map-key", "key-store", "yes");
            string mapKey = MapKeyOf(a);
            string dir = a.Require("key-store");
            if (!a.Flag("yes"))
                throw new UsageException("rotate-key needs --yes: rotating the content key of " + mapKey
                    + " makes EVERY released bundle of that map unreadable for any new key file; the bundles must be re-encrypted from work/build and released again");
            string aside;
            KeyStoreEntry fresh = KeyStore.Rotate(dir, mapKey, out aside);
            Console.Out.WriteLine("OK rotate-key " + mapKey + ": new content key stored in " + fresh.Path + (aside == null ? "" : "; the previous store file is kept as " + aside)
                + ". Re-encrypt the map's bundles and release them again.");
            return 0;
        }

        // ---- bench --------------------------------------------------------------------------------------------------

        private static int Bench(Args a)
        {
            a.Check("provider", "size-mib");
            int mib = a.Has("size-mib") ? (int)a.GetLong("size-mib") : 64;
            if (mib < 1 || mib > 512)
                throw new UsageException("--size-mib must be 1..512");
            List<ICryptoProvider> providers = new List<ICryptoProvider>();
            string choice = a.Get("provider") ?? "both";
            if (choice == "both" || choice == "cng")
            {
                if (CryptoProviders.Cng != null) providers.Add(CryptoProviders.Cng);
                else if (choice == "cng") throw new ToolFailure("Windows CNG is unavailable (" + CryptoProviders.CngUnavailableReason + ")");
            }
            if (choice == "both" || choice == "managed")
                providers.Add(CryptoProviders.Managed);
            if (providers.Count == 0)
                throw new UsageException("--provider must be cng, managed or both");
            byte[] data = new byte[mib << 20];
            new Random(1).NextBytes(data);
            byte[] key = new byte[32];
            byte[] nonce = new byte[8];
            foreach (ICryptoProvider p in providers)
            {
                double aes = Best(delegate
                {
                    using (IAesKey k = p.NewAes(key))
                        AesCtr.Transform(k, nonce, 0, data, 0, data, 0, data.Length);
                }, mib);
                double sha = Best(delegate
                {
                    using (ISha256Stream h = p.NewSha256())
                    {
                        for (int off = 0; off < data.Length; off += 1 << 20)
                            h.Update(data, off, 1 << 20);
                        h.Finish();
                    }
                }, mib);
                double hmac = Best(delegate
                {
                    using (IHashStream h = p.NewHmacSha256(key))
                    {
                        for (int off = 0; off < data.Length; off += 1 << 20)
                            h.Update(data, off, 1 << 20);
                        h.Finish();
                    }
                }, mib);
                Console.Out.WriteLine("BENCH " + p.Name + " (" + mib + " MiB, best of 3, MiB/s): aes-ctr " + Math.Round(aes) + ", sha256 " + Math.Round(sha) + ", hmac-sha256 " + Math.Round(hmac));
            }
            Console.Out.WriteLine("OK bench " + providers.Count + " provider(s)");
            return 0;
        }

        private static double Best(Action work, int mib)
        {
            double best = 0;
            for (int i = 0; i < 3; i++)
            {
                long t = Stopwatch.GetTimestamp();
                work();
                double seconds = Util.Ms(t) / 1000.0;
                if (seconds > 0 && mib / seconds > best)
                    best = mib / seconds;
            }
            return best;
        }
    }
}
