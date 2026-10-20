#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ArenaMapPort.Crypto
{
    /// <summary>Access to the files of the player's Arena installation, injectable so the resolution is testable without one.</summary>
    public interface IArenaFiles
    {
        /// <summary>The size of the file at the plain relative path (key file spelling, '/' separated), or -1 when it does not exist. Other failures throw IOException or UnauthorizedAccessException.</summary>
        long Length(string relativePath);
        /// <summary>Opens the file for reading: a seekable stream (the key sample reads blocks at offsets), shared with a running game.</summary>
        Stream Open(string relativePath);
    }

    /// <summary>
    /// The real <see cref="IArenaFiles"/>: relative paths below one installation folder. Every path passes <see cref="KeyPath.IsSafe"/> and the resolved
    /// full path must still start with the installation folder (two independent guards, as ArenaInstallation.ResolveInside), files open with
    /// FileShare.ReadWrite | Delete so a running Arena does not block them. Used by the client and the build tool.
    /// </summary>
    public sealed class DirectoryArenaFiles : IArenaFiles
    {
        readonly string _root;
        readonly string _prefix;

        public DirectoryArenaFiles(string installDirectory)
        {
            if (string.IsNullOrWhiteSpace(installDirectory))
                throw new ArgumentException("the installation folder is empty", "installDirectory");
            _root = Path.GetFullPath(installDirectory);
            _prefix = _root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? _root : _root + Path.DirectorySeparatorChar;
        }

        string Resolve(string relativePath)
        {
            if (!KeyPath.IsSafe(relativePath))
                throw new IOException("'" + relativePath + "' is not a plain relative path");
            string full = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            if (full.Length <= _prefix.Length || !full.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                throw new IOException("'" + relativePath + "' resolves outside the installation folder");
            return full;
        }

        public long Length(string relativePath)
        {
            FileInfo info = new FileInfo(Resolve(relativePath));
            return info.Exists ? info.Length : -1;
        }

        public Stream Open(string relativePath)
        {
            return new FileStream(Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        }
    }

    /// <summary>Why a content key could not be recovered (the E-lines of docs/PLAN-ENCRYPTION.md 0.9).</summary>
    public enum KeyFailure
    {
        None,
        /// <summary>E1: the key file does not exist.</summary>
        KeyFileMissing,
        /// <summary>E2: the key file exists but is not valid.</summary>
        KeyFileInvalid,
        /// <summary>E3: a listed Arena file is missing or cannot be read.</summary>
        ArenaFileUnreadable,
        /// <summary>E4: a listed Arena file has another size than the key file lists (another Arena build).</summary>
        SizeDiffers,
        /// <summary>E5: the sizes match but the files do not unlock the key (another build with equal sizes, or modified files).</summary>
        ContentDiffers,
        /// <summary>E6: an unexpected exception during derivation.</summary>
        Internal,
    }

    /// <summary>
    /// The outcome of <see cref="KeyResolver.Resolve"/>: the content key, or the cause and the facts the log line needs. The fields that describe a
    /// failure are set for the cause that applies; <see cref="Detail"/> is a readable one-line reason. No hash and no key material appears in any text.
    /// </summary>
    public sealed class KeyResolution
    {
        internal KeyResolution() { }

        public bool Ok { get; internal set; }
        /// <summary>K_map (32 bytes) when <see cref="Ok"/>, otherwise null. Never log it.</summary>
        public byte[] MapKey { get; internal set; }
        public KeyFailure Cause { get; internal set; }
        public string Detail { get; internal set; }
        /// <summary>The key file's built_for of the wrap that applied (success) or of the wrap the failure is reported against (the newest).</summary>
        public string BuiltFor { get; internal set; }
        /// <summary>The Arena version the caller said is installed (informational, for the "built for X but Y is installed" wording).</summary>
        public string InstalledVersion { get; internal set; }
        /// <summary>The file the failure names (SizeDiffers, ArenaFileUnreadable), key file spelling.</summary>
        public string FailedPath { get; internal set; }
        public long ActualSize { get; internal set; }
        public long ExpectedSize { get; internal set; }
        /// <summary>Index of the wrap that unlocked the key, or -1.</summary>
        public int WrapIndex { get; internal set; }
        /// <summary>How many distinct files were read (a file shared by several wraps counts once).</summary>
        public int FilesHashed { get; internal set; }
        /// <summary>How many bytes of those files were read and hashed (the samples, docs/PLAN-ENCRYPTION.md section 6).</summary>
        public long BytesHashed { get; internal set; }
        /// <summary>The total size of the files the unlocking wrap lists (what a whole-file hash would have read); 0 on failure.</summary>
        public long BytesListed { get; internal set; }
        /// <summary>Wall time of the whole resolution in milliseconds.</summary>
        public int Ms { get; internal set; }
        /// <summary>"CNG" or "managed".</summary>
        public string Provider { get; internal set; }
    }

    /// <summary>The three values derived from the Arena files for one wrap: the cipher key, the MAC key and the CTR nonce of the key wrap.</summary>
    public sealed class Kek
    {
        internal Kek(byte[] enc, byte[] mac, byte[] nonce)
        {
            Enc = enc;
            Mac = mac;
            Nonce = nonce;
        }

        public byte[] Enc { get; private set; }
        public byte[] Mac { get; private set; }
        public byte[] Nonce { get; private set; }

        public void Clear()
        {
            Array.Clear(Enc, 0, Enc.Length);
            Array.Clear(Mac, 0, Mac.Length);
            Array.Clear(Nonce, 0, Nonce.Length);
        }
    }

    /// <summary>A listed Arena file has another size than the key file lists, found while it was read (it changed after the size check).</summary>
    public sealed class ArenaFileChangedException : IOException
    {
        public ArenaFileChangedException(string path, long actual, long expected)
            : base("'" + path + "' changed size while it was read (" + actual.ToString(CultureInfo.InvariantCulture) + " against " + expected.ToString(CultureInfo.InvariantCulture) + " bytes)")
        {
            Path = path;
            Actual = actual;
            Expected = expected;
        }

        public string Path { get; private set; }
        public long Actual { get; private set; }
        public long Expected { get; private set; }
    }

    /// <summary>
    /// Key derivation and key wrap (docs/PLAN-ENCRYPTION.md 0.2, with the key source of section 6). Every file a wrap lists contributes one 32-byte digest,
    /// SHA-256 of <see cref="SampleHeader"/> (a label, the file's path and size and the sample parameters) followed by the file's sample
    /// (<see cref="KeySample"/>: evenly spaced blocks whose offsets depend only on the size, or the whole file when it is small); the digests in key-file order
    /// are the input keying material; HKDF (salt from the key file, info "ArenaMapPort bundle key v2 &lt;map_key&gt;") gives 72 bytes: KEK_enc, KEK_mac and the
    /// wrap nonce; the wrapped content key is K_map under AES-256-CTR with a check value HMAC(KEK_mac, label, map key, wrapped key) that is verified in
    /// constant time BEFORE the key is decrypted. The wrap is deterministic (the salt is an HMAC of the entry's inputs under K_map), so a rebuild with the
    /// same inputs gives a byte-identical key file. <see cref="Resolve"/> is the player-side algorithm (pure: file access is injected, it never logs and
    /// never throws), <see cref="Wrap"/> the build-side one. Proved by BundleCryptoTests (pinned labels, a golden sample digest and a golden wrap from
    /// independent implementations, wrap round trip, determinism, sensitivity to order, size, sample parameters and one changed byte inside the sample,
    /// several wraps, every failure cause).
    /// </summary>
    public static class KeyResolver
    {
        const int ReadSize = 1 << 20;

        /// <summary>The HKDF info string of a map: "ArenaMapPort bundle key v2 &lt;map_key&gt;" (UTF-8).</summary>
        public static byte[] KekInfo(string mapKey)
        {
            return Encoding.UTF8.GetBytes("ArenaMapPort bundle key v2 " + mapKey);
        }

        /// <summary>
        /// What is hashed in front of a file's sample: "ArenaMapPort file sample v2" 00 path 00 size 00 block_size 00 blocks 00, the path in key-file spelling
        /// and the numbers as decimal ASCII. It binds each digest to the file it names and to how that file was read.
        /// </summary>
        public static byte[] SampleHeader(string path, long size, KeySample sample)
        {
            if (path == null) throw new ArgumentNullException("path");
            if (sample == null) throw new ArgumentNullException("sample");
            using (MemoryStream data = new MemoryStream())
            {
                Append(data, "ArenaMapPort file sample v2");
                data.WriteByte(0);
                Append(data, path);
                data.WriteByte(0);
                Append(data, size.ToString(CultureInfo.InvariantCulture));
                data.WriteByte(0);
                Append(data, sample.BlockSize.ToString(CultureInfo.InvariantCulture));
                data.WriteByte(0);
                Append(data, sample.Blocks.ToString(CultureInfo.InvariantCulture));
                data.WriteByte(0);
                return data.ToArray();
            }
        }

        /// <summary>
        /// The digest a file contributes to the input keying material: SHA-256(<see cref="SampleHeader"/> || sample), where the sample is the whole file when it
        /// is at most <see cref="KeySample.BytesPerFile"/> bytes, otherwise the <see cref="KeySample.Blocks"/> blocks at <see cref="KeySample.BlockOffset"/> in
        /// ascending order (the stream must then be seekable). <paramref name="bytesRead"/> receives the number of file bytes hashed. A stream that does not hold
        /// exactly <paramref name="size"/> bytes throws <see cref="ArenaFileChangedException"/>.
        /// </summary>
        public static byte[] FileDigest(Stream stream, string path, long size, KeySample sample, ICryptoProvider provider, out long bytesRead)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (sample == null) throw new ArgumentNullException("sample");
            string problem = sample.Problem();
            if (problem != null) throw new ArgumentException("sample " + problem, "sample");
            if (size < 0) throw new ArgumentOutOfRangeException("size");
            if (provider == null) provider = CryptoProviders.Select(null);
            byte[] header = SampleHeader(path, size, sample);
            long total = 0;
            using (ISha256Stream hash = provider.NewSha256())
            {
                hash.Update(header, 0, header.Length);
                if (sample.ReadsWholeFile(size))
                {
                    byte[] buffer = new byte[(int)Math.Min(ReadSize, size + 1)];
                    int n;
                    // reads at most one byte past the listed size: a file that grew is detected without hashing all of it
                    while (total <= size && (n = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        hash.Update(buffer, 0, n);
                        total += n;
                    }
                    if (total != size)
                        throw new ArenaFileChangedException(path, stream.CanSeek ? stream.Length : total, size);
                }
                else
                {
                    long length = stream.Length;
                    if (length != size)
                        throw new ArenaFileChangedException(path, length, size);
                    byte[] buffer = new byte[sample.BlockSize];
                    for (int k = 0; k < sample.Blocks; k++)
                    {
                        stream.Position = sample.BlockOffset(size, k);
                        int got = 0;
                        while (got < buffer.Length)
                        {
                            int n = stream.Read(buffer, got, buffer.Length - got);
                            if (n <= 0)
                                break;
                            got += n;
                        }
                        if (got != buffer.Length)
                            throw new ArenaFileChangedException(path, stream.Length, size);
                        hash.Update(buffer, 0, got);
                        total += got;
                    }
                }
                bytesRead = total;
                return hash.Finish();
            }
        }

        /// <summary>The digest of one listed file, opened through <paramref name="files"/> (see the stream overload).</summary>
        public static byte[] FileDigest(IArenaFiles files, KeyFileEntry entry, KeySample sample, ICryptoProvider provider, out long bytesRead)
        {
            if (files == null) throw new ArgumentNullException("files");
            if (entry == null) throw new ArgumentNullException("entry");
            using (Stream s = files.Open(entry.Path))
                return FileDigest(s, entry.Path, entry.Size, sample, provider, out bytesRead);
        }

        /// <summary>The key-wrapping key from an IKM: HKDF-Extract(salt, ikm), HKDF-Expand(info of <paramref name="mapKey"/>, 72) split into 32 + 32 + 8 bytes.</summary>
        public static Kek DeriveKek(byte[] ikm, byte[] salt, string mapKey, ICryptoProvider provider)
        {
            if (ikm == null) throw new ArgumentNullException("ikm");
            if (salt == null) throw new ArgumentNullException("salt");
            if (provider == null) provider = CryptoProviders.Select(null);
            byte[] prk = Hkdf.Extract(provider, salt, ikm);
            byte[] okm = Hkdf.Expand(provider, prk, KekInfo(mapKey), 72);
            byte[] enc = new byte[32], mac = new byte[32], nonce = new byte[8];
            Buffer.BlockCopy(okm, 0, enc, 0, 32);
            Buffer.BlockCopy(okm, 32, mac, 0, 32);
            Buffer.BlockCopy(okm, 64, nonce, 0, 8);
            Array.Clear(prk, 0, prk.Length);
            Array.Clear(okm, 0, okm.Length);
            return new Kek(enc, mac, nonce);
        }

        /// <summary>
        /// Verifies the wrap's check value (constant time) and, only then, decrypts the content key. Returns K_map, or null when the check does
        /// not verify (these Arena files do not unlock this wrap).
        /// </summary>
        public static byte[] Unwrap(KeyWrap wrap, string mapKey, Kek kek, ICryptoProvider provider)
        {
            if (wrap == null) throw new ArgumentNullException("wrap");
            if (kek == null) throw new ArgumentNullException("kek");
            if (provider == null) provider = CryptoProviders.Select(null);
            byte[] expected = ComputeCheck(mapKey, wrap.WrappedKey, kek, provider);
            if (!ConstantTime.AreEqual(expected, wrap.Check))
                return null;
            byte[] kMap = new byte[32];
            using (IAesKey aes = provider.NewAes(kek.Enc))
                AesCtr.Transform(aes, kek.Nonce, 0, wrap.WrappedKey, 0, kMap, 0, 32);
            return kMap;
        }

        /// <summary>
        /// Builds the wrap of <paramref name="kMap"/> for one Arena build: <paramref name="fileDigests"/> are the <see cref="FileDigest"/> values of
        /// <paramref name="files"/> under <paramref name="sample"/>, in that order (the digests are used and never stored). The salt is deterministic:
        /// HMAC(K_map, "ArenaMapPort wrap salt v2" 00 map_key 00 built_for 00 block_size 00 blocks 00 (path 00 size 00)*).
        /// </summary>
        public static KeyWrap Wrap(string mapKey, byte[] kMap, string builtFor, KeySample sample, IList<KeyFileEntry> files, byte[][] fileDigests, ICryptoProvider provider = null)
        {
            Ampb.RequireKey(kMap);
            if (!Ampb.IsValidMapKey(mapKey)) throw new ArgumentException("invalid map key", "mapKey");
            if (builtFor == null) throw new ArgumentNullException("builtFor");
            if (sample == null) throw new ArgumentNullException("sample");
            if (sample.Problem() != null) throw new ArgumentException("sample " + sample.Problem(), "sample");
            if (files == null) throw new ArgumentNullException("files");
            if (fileDigests == null || fileDigests.Length != files.Count) throw new ArgumentException("one digest per file is required", "fileDigests");
            if (provider == null) provider = CryptoProviders.Select(null);

            byte[] ikm = new byte[32 * files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                if (fileDigests[i] == null || fileDigests[i].Length != 32)
                    throw new ArgumentException("every file digest is 32 bytes", "fileDigests");
                Buffer.BlockCopy(fileDigests[i], 0, ikm, 32 * i, 32);
            }

            byte[] salt = ComputeSalt(mapKey, kMap, builtFor, sample, files, provider);
            Kek kek = DeriveKek(ikm, salt, mapKey, provider);
            try
            {
                byte[] wrapped = new byte[32];
                using (IAesKey aes = provider.NewAes(kek.Enc))
                    AesCtr.Transform(aes, kek.Nonce, 0, kMap, 0, wrapped, 0, 32);
                byte[] check = ComputeCheck(mapKey, wrapped, kek, provider);
                List<KeyFileEntry> copy = new List<KeyFileEntry>(files.Count);
                foreach (KeyFileEntry e in files)
                    copy.Add(new KeyFileEntry(e.Path, e.Size));
                return new KeyWrap(builtFor, new KeySample(sample.BlockSize, sample.Blocks), copy, salt, wrapped, check);
            }
            finally
            {
                kek.Clear();
                Array.Clear(ikm, 0, ikm.Length);
            }
        }

        static byte[] ComputeSalt(string mapKey, byte[] kMap, string builtFor, KeySample sample, IList<KeyFileEntry> files, ICryptoProvider provider)
        {
            using (MemoryStream data = new MemoryStream())
            {
                Append(data, "ArenaMapPort wrap salt v2");
                data.WriteByte(0);
                Append(data, mapKey);
                data.WriteByte(0);
                Append(data, builtFor);
                data.WriteByte(0);
                Append(data, sample.BlockSize.ToString(CultureInfo.InvariantCulture));
                data.WriteByte(0);
                Append(data, sample.Blocks.ToString(CultureInfo.InvariantCulture));
                data.WriteByte(0);
                foreach (KeyFileEntry e in files)
                {
                    Append(data, e.Path);
                    data.WriteByte(0);
                    Append(data, e.Size.ToString(CultureInfo.InvariantCulture));
                    data.WriteByte(0);
                }
                byte[] bytes = data.ToArray();
                return provider.HmacSha256(kMap, bytes, 0, bytes.Length);
            }
        }

        static byte[] ComputeCheck(string mapKey, byte[] wrappedKey, Kek kek, ICryptoProvider provider)
        {
            using (MemoryStream data = new MemoryStream())
            {
                Append(data, "ArenaMapPort wrap check v2");
                data.WriteByte(0);
                Append(data, mapKey);
                data.WriteByte(0);
                data.Write(wrappedKey, 0, wrappedKey.Length);
                byte[] bytes = data.ToArray();
                return provider.HmacSha256(kek.Mac, bytes, 0, bytes.Length);
            }
        }

        static void Append(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        /// <summary>
        /// Loads the key file at <paramref name="keyFilePath"/> and resolves it (see <see cref="Resolve"/>); a missing file is KeyFileMissing, an
        /// unreadable or invalid one KeyFileInvalid.
        /// </summary>
        public static KeyResolution ResolveFile(string keyFilePath, string installDirectory, string installedVersion, IArenaFiles files, ICryptoProvider provider, string expectedMapKey = null)
        {
            KeyFile keyFile;
            try
            {
                keyFile = KeyFile.Load(keyFilePath);
            }
            catch (FileNotFoundException ex)
            {
                return Failure(KeyFailure.KeyFileMissing, "key file '" + keyFilePath + "' is missing", provider, ex);
            }
            catch (DirectoryNotFoundException ex)
            {
                return Failure(KeyFailure.KeyFileMissing, "key file '" + keyFilePath + "' is missing", provider, ex);
            }
            catch (KeyFileException ex)
            {
                return Failure(KeyFailure.KeyFileInvalid, ex.Message, provider, ex);
            }
            catch (Exception ex)
            {
                if (ex is IOException || ex is UnauthorizedAccessException)
                    return Failure(KeyFailure.KeyFileInvalid, "the key file cannot be read (" + ex.Message + ")", provider, ex);
                return Failure(KeyFailure.Internal, ex.GetType().Name + ": " + ex.Message, provider, ex);
            }
            return Resolve(keyFile, installDirectory, installedVersion, files, provider, expectedMapKey);
        }

        /// <summary>
        /// The player-side algorithm. Wraps are tried in file order; a wrap applies when all its files exist with the listed sizes; the first
        /// applicable wrap whose check verifies wins and yields K_map. Otherwise the cause is ContentDiffers when some wrap applied but no
        /// check verified (right sizes, wrong content), else the failure of the LAST wrap (the newest build): ArenaFileUnreadable or SizeDiffers.
        /// Never throws: an unexpected exception becomes Internal. Digests are cached per file and sample, so wraps that share them read once.
        /// </summary>
        public static KeyResolution Resolve(KeyFile keyFile, string installDirectory, string installedVersion, IArenaFiles files, ICryptoProvider provider, string expectedMapKey = null)
        {
            long started = Stopwatch.GetTimestamp();
            ICryptoProvider p = provider ?? CryptoProviders.Select(null);
            KeyResolution result = new KeyResolution();
            result.Provider = p.Name;
            result.InstalledVersion = installedVersion;
            result.WrapIndex = -1;
            try
            {
                if (keyFile == null)
                    return Finish(result, started, KeyFailure.KeyFileInvalid, "no key file");
                if (expectedMapKey != null && !string.Equals(expectedMapKey, keyFile.MapKey, StringComparison.Ordinal))
                    return Finish(result, started, KeyFailure.KeyFileInvalid, "the key file is for '" + keyFile.MapKey + "', not for '" + expectedMapKey + "'");
                if (keyFile.Wraps.Count == 0)
                    return Finish(result, started, KeyFailure.KeyFileInvalid, "the key file lists no wraps");
                if (files == null)
                    return Finish(result, started, KeyFailure.Internal, "no file access was given");

                Dictionary<string, byte[]> digests = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                bool someApplied = false;
                KeyFailure lastCause = KeyFailure.None;
                string lastDetail = null;
                for (int w = 0; w < keyFile.Wraps.Count; w++)
                {
                    KeyWrap wrap = keyFile.Wraps[w];
                    result.BuiltFor = wrap.BuiltFor;
                    KeyFailure cause;
                    string detail;
                    string failedPath;
                    long actual, expected;
                    if (!Applies(wrap, files, out cause, out detail, out failedPath, out actual, out expected))
                    {
                        lastCause = cause;
                        lastDetail = detail;
                        result.FailedPath = failedPath;
                        result.ActualSize = actual;
                        result.ExpectedSize = expected;
                        continue;
                    }
                    byte[] ikm;
                    try
                    {
                        ikm = Ikm(wrap, files, p, digests, result);
                    }
                    catch (ArenaFileChangedException ex)
                    {
                        // the sizes did not hold while the files were read: a size failure, not "right sizes, wrong content"
                        lastCause = KeyFailure.SizeDiffers;
                        lastDetail = ex.Message;
                        result.FailedPath = ex.Path;
                        result.ActualSize = ex.Actual;
                        result.ExpectedSize = ex.Expected;
                        continue;
                    }
                    catch (FileReadException ex)
                    {
                        return FinishUnreadable(result, started, ex.Message, ex.Path);
                    }
                    someApplied = true;
                    Kek kek = DeriveKek(ikm, wrap.Salt, keyFile.MapKey, p);
                    byte[] kMap;
                    try
                    {
                        kMap = Unwrap(wrap, keyFile.MapKey, kek, p);
                    }
                    finally
                    {
                        kek.Clear();
                        Array.Clear(ikm, 0, ikm.Length);
                    }
                    if (kMap != null)
                    {
                        long listed = 0;
                        foreach (KeyFileEntry e in wrap.Files)
                            listed += e.Size;
                        result.Ok = true;
                        result.MapKey = kMap;
                        result.Cause = KeyFailure.None;
                        result.WrapIndex = w;
                        result.BuiltFor = wrap.BuiltFor;
                        result.BytesListed = listed;
                        result.FailedPath = null;
                        result.ActualSize = 0;
                        result.ExpectedSize = 0;
                        result.Detail = null;
                        result.Ms = Elapsed(started);
                        return result;
                    }
                }

                KeyWrap newest = keyFile.Wraps[keyFile.Wraps.Count - 1];
                result.BuiltFor = newest.BuiltFor;
                if (someApplied)
                {
                    result.FailedPath = null;
                    result.ActualSize = 0;
                    result.ExpectedSize = 0;
                    return Finish(result, started, KeyFailure.ContentDiffers,
                        "the Arena files have the listed sizes but do not unlock the key (another build with equal sizes, or modified files)");
                }
                return Finish(result, started, lastCause, lastDetail);
            }
            catch (Exception ex)
            {
                return Finish(result, started, KeyFailure.Internal, ex.GetType().Name + ": " + ex.Message);
            }
        }

        sealed class FileReadException : Exception
        {
            public FileReadException(string path, string reason) : base(reason)
            {
                Path = path;
            }

            public string Path { get; private set; }
        }

        static bool Applies(KeyWrap wrap, IArenaFiles files, out KeyFailure cause, out string detail, out string failedPath, out long actual, out long expected)
        {
            cause = KeyFailure.None;
            detail = null;
            failedPath = null;
            actual = 0;
            expected = 0;
            foreach (KeyFileEntry entry in wrap.Files)
            {
                long length;
                try
                {
                    length = files.Length(entry.Path);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException))
                        throw;
                    cause = KeyFailure.ArenaFileUnreadable;
                    detail = ex.Message;
                    failedPath = entry.Path;
                    return false;
                }
                if (length < 0)
                {
                    cause = KeyFailure.ArenaFileUnreadable;
                    detail = "the file does not exist";
                    failedPath = entry.Path;
                    return false;
                }
                if (length != entry.Size)
                {
                    cause = KeyFailure.SizeDiffers;
                    failedPath = entry.Path;
                    actual = length;
                    expected = entry.Size;
                    detail = "'" + entry.Path + "' is " + length.ToString(CultureInfo.InvariantCulture) + " bytes, " + entry.Size.ToString(CultureInfo.InvariantCulture) + " expected";
                    return false;
                }
            }
            return true;
        }

        static byte[] Ikm(KeyWrap wrap, IArenaFiles files, ICryptoProvider p, Dictionary<string, byte[]> cache, KeyResolution stats)
        {
            byte[] ikm = new byte[32 * wrap.Files.Count];
            for (int i = 0; i < wrap.Files.Count; i++)
            {
                KeyFileEntry entry = wrap.Files[i];
                byte[] digest;
                string cacheKey = entry.Path + "\n" + entry.Size.ToString(CultureInfo.InvariantCulture) + "\n" +
                                  wrap.Sample.BlockSize.ToString(CultureInfo.InvariantCulture) + "\n" + wrap.Sample.Blocks.ToString(CultureInfo.InvariantCulture);
                if (!cache.TryGetValue(cacheKey, out digest))
                {
                    long read;
                    try
                    {
                        digest = FileDigest(files, entry, wrap.Sample, p, out read);
                    }
                    catch (ArenaFileChangedException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (ex is IOException || ex is UnauthorizedAccessException)
                            throw new FileReadException(entry.Path, ex.Message);
                        throw;
                    }
                    cache[cacheKey] = digest;
                    stats.FilesHashed++;
                    stats.BytesHashed += read;
                }
                Buffer.BlockCopy(digest, 0, ikm, 32 * i, 32);
            }
            return ikm;
        }

        static KeyResolution FinishUnreadable(KeyResolution r, long started, string reason, string path)
        {
            r.FailedPath = path ?? r.FailedPath;
            return Finish(r, started, KeyFailure.ArenaFileUnreadable, reason);
        }

        static KeyResolution Finish(KeyResolution r, long started, KeyFailure cause, string detail)
        {
            r.Ok = false;
            r.MapKey = null;
            r.Cause = cause;
            r.Detail = detail;
            r.BytesListed = 0;
            r.Ms = Elapsed(started);
            return r;
        }

        static KeyResolution Failure(KeyFailure cause, string detail, ICryptoProvider provider, Exception ex)
        {
            KeyResolution r = new KeyResolution();
            r.Cause = cause;
            r.Detail = detail;
            r.WrapIndex = -1;
            r.Provider = provider == null ? null : provider.Name;
            return r;
        }

        static int Elapsed(long started)
        {
            return (int)((Stopwatch.GetTimestamp() - started) * 1000L / Stopwatch.Frequency);
        }
    }
}
