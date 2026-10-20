#nullable disable
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using ArenaMapPort.Shared;
using Xunit;
using ClientGate = ArenaMapPort.Client.ArenaInstallationGate;

namespace ArenaMapPort.Server.Tests;

// Tests for the client side of the bundle encryption (docs/PLAN-ENCRYPTION.md, area CLIENT): the per-map key resolution of
// BundleKeyResolver (stage-1 gate, key file, Arena resource files and their sample, cache, the exact E1-E9 / W1 / D1-D3 lines), the opening of an AMPB container
// into a decrypting stream (Prepare), the stream registry that closes the streams Unity keeps open, and the source-level guards for the two
// loaders (BundleHost.LoadAssetBundle, NavMeshInstaller). The loaders themselves call Unity and cannot run here; everything they decide is
// in BundleKeyResolver.cs, which is linked into this assembly. The Crypto folder is the shipped code, linked too.

/// <summary>A scratch Arena installation with three resource files (two read whole, one through its sample), a keys folder and a bundles folder in
/// one temp directory, plus builders for wraps, key files and encrypted bundles.</summary>
internal sealed class KeyBed : IDisposable
{
    internal const string Map = "arena_test";
    internal const string Other = "arena_other";
    internal const string Version = "0.6.1.0.48206";
    internal const string OlderVersion = "0.6.0.0.47752";

    internal static readonly string[] Sources =
    {
        "EscapeFromTarkovArena_Data/sharedassets41.assets.resS",
        "EscapeFromTarkovArena_Data/sharedassets41.resource",
        "EscapeFromTarkovArena_Data/sharedassets45.assets.resS",
    };

    internal static readonly int[] Sizes = { 5000, 1_200_000, 100 };

    /// <summary>4 blocks of 64 KiB: the 1.2 MB file is read through its 256 KiB sample, the other two whole.</summary>
    internal static readonly KeySample Sample = new(65536, 4);

    internal static readonly byte[] KMap = Enumerable.Range(0, 32).Select(i => (byte)(i * 11 + 3)).ToArray();
    internal static readonly byte[] OtherKMap = Enumerable.Range(0, 32).Select(i => (byte)(i * 5 + 9)).ToArray();

    internal readonly string Root = Path.Combine(Path.GetTempPath(), "ampb-client-" + Guid.NewGuid().ToString("N"));
    internal readonly string Install;
    internal readonly string Keys;
    internal readonly string Bundles;

    internal KeyBed()
    {
        Install = Path.Combine(Root, "Arena");
        Keys = Path.Combine(Root, "keys");
        Bundles = Path.Combine(Root, "bundles");
        Directory.CreateDirectory(Path.Combine(Install, "EscapeFromTarkovArena_Data"));
        Directory.CreateDirectory(Keys);
        Directory.CreateDirectory(Bundles);
        for (int i = 0; i < Sources.Length; i++)
            File.WriteAllBytes(SourcePath(i), Bytes(Sizes[i], 100 + i));
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); }
        catch (Exception) { /* best effort */ }
    }

    internal static byte[] Bytes(int length, int seed)
    {
        byte[] b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    internal static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    internal string SourcePath(int index) => Path.Combine(Install, Sources[index].Replace('/', Path.DirectorySeparatorChar));

    internal string KeyFilePath(string map = Map) => Path.Combine(Keys, map + ".json");

    /// <summary>The wrap of <paramref name="kMap"/> for the files as they are on disk right now.</summary>
    internal KeyWrap MakeWrap(string builtFor = Version, byte[] kMap = null, string map = Map)
    {
        var entries = new List<KeyFileEntry>();
        var digests = new List<byte[]>();
        for (int i = 0; i < Sources.Length; i++)
        {
            var entry = new KeyFileEntry(Sources[i], new FileInfo(SourcePath(i)).Length);
            entries.Add(entry);
            digests.Add(SampleDigest(entry));
        }
        return KeyResolver.Wrap(map, kMap ?? KMap, builtFor, Sample, entries, digests.ToArray(), CryptoProviders.Managed);
    }

    /// <summary>The digest a file contributes to the key (what must never appear in a log line).</summary>
    internal byte[] SampleDigest(KeyFileEntry entry)
    {
        return KeyResolver.FileDigest(new DirectoryArenaFiles(Install), entry, Sample, CryptoProviders.Managed, out _);
    }

    internal void WriteKeyFile(string map, params KeyWrap[] wraps)
    {
        var file = new KeyFile(map);
        foreach (var wrap in wraps)
            file.Wraps.Add(wrap);
        file.Save(KeyFilePath(map));
    }

    /// <summary>Writes the key file of <see cref="Map"/> for the files as they are now.</summary>
    internal void WriteKeyFile() => WriteKeyFile(Map, MakeWrap());

    internal string WriteBundle(string fileName, byte[] plain, int chunkSize = 4096, byte[] kMap = null, string map = Map)
    {
        string path = Path.Combine(Bundles, fileName);
        using (var dest = File.Create(path))
            Ampb.Encrypt(new MemoryStream(plain), dest, map, kMap ?? KMap, CryptoProviders.Managed, chunkSize);
        return path;
    }

    internal static void FlipByte(string path, long offset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        fs.Position = offset;
        int b = fs.ReadByte();
        fs.Position = offset;
        fs.WriteByte((byte)(b ^ 0x40));
    }

    internal static void Truncate(string path, int bytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        fs.SetLength(fs.Length - bytes);
    }

    internal static byte[] ReadAll(Stream stream)
    {
        stream.Position = 0;
        var result = new MemoryStream();
        stream.CopyTo(result);
        return result.ToArray();
    }
}

/// <summary>Arena files that count what is asked of them (the thread-safe counters of the concurrency and "nothing is read" tests).</summary>
internal sealed class CountingArenaFiles : IArenaFiles
{
    readonly IArenaFiles _inner;
    internal int Lengths;
    internal int Opens;
    internal readonly ConcurrentQueue<string> Opened = new();

    internal CountingArenaFiles(IArenaFiles inner) => _inner = inner;

    public long Length(string relativePath)
    {
        Interlocked.Increment(ref Lengths);
        return _inner.Length(relativePath);
    }

    public Stream Open(string relativePath)
    {
        Interlocked.Increment(ref Opens);
        Opened.Enqueue(relativePath);
        return _inner.Open(relativePath);
    }
}

/// <summary>The injectable surroundings of <see cref="BundleKeyResolver"/>: a switchable stage-1 gate, a temp keys folder, counting file access
/// and recording log queues (thread-safe, the concurrency test calls it from eight threads).</summary>
internal sealed class FakeKeyEnvironment : IBundleKeyEnvironment
{
    readonly KeyBed _bed;
    internal bool Verified = true;
    internal string VersionValue = KeyBed.Version;
    internal Func<string, IArenaFiles> FilesFactory;
    internal Func<Action<string>, ICryptoProvider> ProviderFactory = _ => CryptoProviders.Managed;
    internal CountingArenaFiles Files;
    internal int EnsureCalls;
    internal int OpenFilesCalls;
    internal readonly ConcurrentQueue<string> Debug = new();
    internal readonly ConcurrentQueue<string> Warnings = new();
    internal readonly ConcurrentQueue<string> Errors = new();
    /// <summary>The managed thread id of every log call, to prove that nothing is logged from a thread the caller did not own.</summary>
    internal readonly ConcurrentQueue<int> LogThreads = new();

    internal FakeKeyEnvironment(KeyBed bed)
    {
        _bed = bed;
        FilesFactory = install => Files = new CountingArenaFiles(new DirectoryArenaFiles(install));
    }

    public bool EnsureVerified()
    {
        Interlocked.Increment(ref EnsureCalls);
        return Verified;
    }

    public string InstallPath => Verified ? _bed.Install : null;
    public string InstalledVersion => VersionValue;
    public string KeysDirectory => _bed.Keys;

    public IArenaFiles OpenArenaFiles(string installPath)
    {
        Interlocked.Increment(ref OpenFilesCalls);
        return FilesFactory(installPath);
    }

    public ICryptoProvider CryptoProvider(Action<string> warning) => ProviderFactory(warning);
    public void LogDebug(string message) { LogThreads.Enqueue(Environment.CurrentManagedThreadId); Debug.Enqueue(message); }
    public void LogWarning(string message) { LogThreads.Enqueue(Environment.CurrentManagedThreadId); Warnings.Enqueue(message); }
    public void LogError(string message) { LogThreads.Enqueue(Environment.CurrentManagedThreadId); Errors.Enqueue(message); }

    internal string[] AllLines() => Debug.Concat(Warnings).Concat(Errors).ToArray();
}

public sealed class BundleKeyResolverTests : IDisposable
{
    const string Map = KeyBed.Map;

    readonly KeyBed _bed = new();

    public void Dispose() => _bed.Dispose();

    static string Head(string map = Map) => "[BundleKeys] key for '" + map + "' unavailable: ";

    static ICryptoProvider Provider(string name) => name == "cng" ? CryptoProviders.Cng : CryptoProviders.Managed;

    (BundleKeyResolver resolver, FakeKeyEnvironment env) Make()
    {
        var env = new FakeKeyEnvironment(_bed);
        return (new BundleKeyResolver(env), env);
    }

    // ---- the key is recovered ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("managed")]
    [InlineData("cng")]
    public void TheKeyIsDerivedFromTheArenaFilesAndTheDebugLineSaysHow(string name)
    {
        ICryptoProvider provider = Provider(name);
        if (provider == null)
            return; // no CNG on this machine
        _bed.WriteKeyFile();
        var (resolver, env) = Make();
        env.ProviderFactory = _ => provider;

        Assert.True(resolver.TryGetKey(Map, out byte[] key));
        Assert.Equal(KeyBed.Hex(KeyBed.KMap), KeyBed.Hex(key));
        Assert.Equal(1, resolver.Derivations);
        Assert.Equal(KeyBed.Sources, env.Files.Opened.ToArray()); // the listed files, once each, in key file order
        string line = Assert.Single(env.Debug);
        Assert.Matches(new Regex(@"^\[BundleKeys\] key for 'arena_test' derived in \d+ ms \(3 files, 0\.3 MB sampled of 1\.2 MB, built for Arena 0\.6\.1\.0\.48206, crypto: " +
                                 provider.Name + @"\)$"), line);
        Assert.Empty(env.Errors);
        Assert.Empty(env.Warnings);
    }

    [Fact]
    public void TheDerivedLineFormatsTheNumbersWithTheInvariantCulture()
    {
        var resolution = ResolveOk();
        string line = BundleKeyResolver.DerivedLine("arena_bowl", resolution);
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string german = BundleKeyResolver.DerivedLine("arena_bowl", resolution);
            Assert.Contains(" files, 0.3 MB sampled of 1.2 MB, built for Arena ", german);
            Assert.Equal(line.Replace(resolution.Ms + " ms", "N ms"), german.Replace(resolution.Ms + " ms", "N ms"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    KeyResolution ResolveOk()
    {
        _bed.WriteKeyFile();
        return KeyResolver.ResolveFile(_bed.KeyFilePath(), _bed.Install, KeyBed.Version, new DirectoryArenaFiles(_bed.Install), CryptoProviders.Managed, Map);
    }

    [Fact]
    public void TheKeyIsCachedForTheSession()
    {
        _bed.WriteKeyFile();
        var (resolver, env) = Make();
        Assert.True(resolver.TryGetKey(Map, out byte[] first));
        int opens = env.Files.Opens;
        Assert.True(resolver.TryGetKey(Map, out byte[] second));
        Assert.Same(first, second);
        Assert.Equal(1, resolver.Derivations);
        Assert.Equal(opens, env.Files.Opens);
        Assert.Equal(1, env.OpenFilesCalls);
        Assert.Single(env.Debug);
    }

    [Fact]
    public void ArenaNotVerifiedReadsNothingLogsNothingAndIsNotCached()
    {
        _bed.WriteKeyFile();
        var (resolver, env) = Make();
        env.Verified = false;

        Assert.False(resolver.TryGetKey(Map, out byte[] key));
        Assert.Null(key);
        Assert.Equal(0, resolver.Derivations);
        Assert.Equal(0, env.OpenFilesCalls); // no Arena file was even looked at
        Assert.Empty(env.AllLines());          // stage 1 owns that message

        env.Verified = true; // the check finished later: the same resolver now derives
        Assert.True(resolver.TryGetKey(Map, out key));
        Assert.Equal(KeyBed.Hex(KeyBed.KMap), KeyBed.Hex(key));
        Assert.Equal(1, resolver.Derivations);
    }

    [Fact]
    public void ANullOrEmptyMapKeyHasNoKey()
    {
        var (resolver, env) = Make();
        Assert.False(resolver.TryGetKey(null, out _));
        Assert.False(resolver.TryGetKey("", out _));
        Assert.Equal(0, env.EnsureCalls);
        Assert.Empty(env.AllLines());
    }

    // ---- every failure cause, with its exact line ---------------------------------------------------------------

    [Fact]
    public void E1AMissingKeyFileIsNamedWithItsPath()
    {
        var (resolver, env) = Make();
        Assert.False(resolver.TryGetKey(Map, out byte[] key));
        Assert.Null(key);
        Assert.Equal(Head() + "key file '" + _bed.KeyFilePath() + "' is missing; reinstall ArenaMapPort (the keys folder belongs to the release)",
            Assert.Single(env.Errors));
        Assert.Empty(env.Debug);
    }

    [Theory]
    [InlineData("", "the key file is empty")]
    [InlineData("garbage", "unexpected character 'g'")]
    [InlineData("[1, 2]", "the key file is not a JSON object")]
    [InlineData("{\"schema\": 1, \"map_key\": \"arena_test\", \"wraps\": []}", "unsupported key file schema 1")] // the retired level-file scheme
    [InlineData("{\"schema\": 3, \"map_key\": \"arena_test\", \"wraps\": []}", "unsupported key file schema 3")]
    [InlineData("{\"schema\": 2, \"map_key\": \"arena_test\", \"wraps\": []}", "the key file lists no wraps")]
    public void E2ACorruptKeyFileSaysWhatIsWrongWithIt(string content, string reason)
    {
        File.WriteAllText(_bed.KeyFilePath(), content);
        var (resolver, env) = Make();
        Assert.False(resolver.TryGetKey(Map, out _));
        string line = Assert.Single(env.Errors);
        Assert.StartsWith(Head() + "key file '" + _bed.KeyFilePath() + "' is not valid (", line);
        Assert.EndsWith("); reinstall ArenaMapPort", line);
        Assert.Contains(reason, line);
    }

    [Fact]
    public void E2AKeyFileOverSixtyFourKiBIsRefused()
    {
        File.WriteAllBytes(_bed.KeyFilePath(), new byte[70 * 1024]);
        var (resolver, env) = Make();
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.Contains("the key file is larger than 64 KiB", Assert.Single(env.Errors));
    }

    [Fact]
    public void E2AKeyFileOfAnotherMapIsRefused()
    {
        _bed.WriteKeyFile(KeyBed.Other, _bed.MakeWrap(map: KeyBed.Other));
        File.Copy(_bed.KeyFilePath(KeyBed.Other), _bed.KeyFilePath(Map));
        var (resolver, env) = Make();
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.Contains("the key file is for 'arena_other', not for 'arena_test'", Assert.Single(env.Errors));
        Assert.Equal(0, env.Files?.Opens ?? 0);
    }

    [Fact]
    public void E3AnArenaFileThatDoesNotExistIsNamedWithTheInstallFolder()
    {
        _bed.WriteKeyFile();
        File.Delete(_bed.SourcePath(1));
        var (resolver, env) = Make();
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.Equal(Head() + "Arena file 'EscapeFromTarkovArena_Data/sharedassets41.resource' cannot be read in " + _bed.Install + " (the file does not exist)",
            Assert.Single(env.Errors));
    }

    [Fact]
    public void E3AnArenaFileThatCannotBeOpenedIsNamedWithTheReason()
    {
        if (!OperatingSystem.IsWindows())
            return; // the sharing violation below is a Windows behaviour
        _bed.WriteKeyFile();
        var (resolver, env) = Make();
        using (new FileStream(_bed.SourcePath(0), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(resolver.TryGetKey(Map, out _));
        string line = Assert.Single(env.Errors);
        Assert.StartsWith(Head() + "Arena file 'EscapeFromTarkovArena_Data/sharedassets41.assets.resS' cannot be read in " + _bed.Install + " (", line);
        Assert.EndsWith(")", line);
    }

    [Fact]
    public void E4AnotherArenaBuildNamesBothVersionsAndTheFile()
    {
        var wrap = _bed.MakeWrap(KeyBed.OlderVersion);
        _bed.WriteKeyFile(Map, wrap);
        File.AppendAllText(_bed.SourcePath(1), "x"); // 1,200,001 bytes now
        var (resolver, env) = Make();
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.Equal(Head() + "the maps were built for Arena 0.6.0.0.47752 but 0.6.1.0.48206 is installed " +
                     "('EscapeFromTarkovArena_Data/sharedassets41.resource' is 1200001 bytes there, 1200000 expected); a keys update for this Arena build is needed",
            Assert.Single(env.Errors));
    }

    [Fact]
    public void E4TheSameVersionWithAnotherSizeSaysItDiffersFromTheKeyFile()
    {
        _bed.WriteKeyFile();
        File.AppendAllText(_bed.SourcePath(1), "x");
        var (resolver, env) = Make();
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.Equal(Head() + "Arena file 'EscapeFromTarkovArena_Data/sharedassets41.resource' differs in size from the key file " +
                     "(1200001 against 1200000 bytes, Arena 0.6.1.0.48206)",
            Assert.Single(env.Errors));
    }

    [Fact]
    public void E4AnUnknownInstalledVersionIsCalledAnotherBuild()
    {
        _bed.WriteKeyFile(Map, _bed.MakeWrap(KeyBed.OlderVersion));
        File.AppendAllText(_bed.SourcePath(2), "x");
        var (resolver, env) = Make();
        env.VersionValue = null;
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.Equal(Head() + "the maps were built for Arena 0.6.0.0.47752 but another build is installed " +
                     "('EscapeFromTarkovArena_Data/sharedassets45.assets.resS' is 101 bytes there, 100 expected); a keys update for this Arena build is needed",
            Assert.Single(env.Errors));
    }

    [Fact]
    public void E5RightSizesButAnotherContentDoNotUnlockTheKey()
    {
        _bed.WriteKeyFile();
        KeyBed.FlipByte(_bed.SourcePath(0), 17); // same size, one byte differs
        var (resolver, env) = Make();
        Assert.False(resolver.TryGetKey(Map, out byte[] key));
        Assert.Null(key);
        Assert.Equal(Head() + "the Arena files have the listed sizes but do not unlock the key (another build with equal sizes, or modified files)",
            Assert.Single(env.Errors));
        Assert.Equal(1, resolver.Derivations);
    }

    [Fact]
    public void E6AnUnexpectedExceptionIsReportedWithItsTypeAndMessage()
    {
        _bed.WriteKeyFile();
        var (resolver, env) = Make();
        env.FilesFactory = _ => throw new InvalidOperationException("boom");
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.Equal(Head() + "key derivation failed (InvalidOperationException: boom)", Assert.Single(env.Errors));
    }

    [Fact]
    public void E6ACryptoProviderThatThrowsIsReportedToo()
    {
        _bed.WriteKeyFile();
        var (resolver, env) = Make();
        env.ProviderFactory = _ => throw new PlatformNotSupportedException("no crypto");
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.Equal(Head() + "key derivation failed (PlatformNotSupportedException: no crypto)", Assert.Single(env.Errors));
    }

    [Fact]
    public void E6AMapKeyThatCannotNameAKeyFileIsRefusedBeforeAnyPathIsBuilt()
    {
        var (resolver, env) = Make();
        Assert.False(resolver.TryGetKey("..\\evil", out _));
        Assert.Contains("key derivation failed (ArgumentException: '..\\evil' is not a valid map key)", Assert.Single(env.Errors));
        Assert.Equal(0, env.OpenFilesCalls);
    }

    [Fact]
    public void AFailureIsCachedAndLoggedOnce()
    {
        var (resolver, env) = Make(); // no key file: E1
        Assert.False(resolver.TryGetKey(Map, out _));
        _bed.WriteKeyFile();           // fixing it mid-session changes nothing: the failure stays for the session
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.Single(env.Errors);
        Assert.Equal(1, env.EnsureCalls);
        Assert.Equal(1, env.OpenFilesCalls);
        Assert.Equal(0, env.Files.Opens); // no Arena file was read for a missing key file, nor after the key file appeared
    }

    [Fact]
    public void AFailedMapDoesNotAffectAnother()
    {
        _bed.WriteKeyFile(KeyBed.Other, _bed.MakeWrap(map: KeyBed.Other, kMap: KeyBed.OtherKMap)); // 'arena_test' has no key file
        var (resolver, env) = Make();
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.True(resolver.TryGetKey(KeyBed.Other, out byte[] key));
        Assert.Equal(KeyBed.Hex(KeyBed.OtherKMap), KeyBed.Hex(key));
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.Single(env.Errors);
        Assert.Single(env.Debug);
        Assert.Contains("'arena_other'", env.Debug.Single());
    }

    // ---- several wraps (Arena updates) -------------------------------------------------------------------------

    [Fact]
    public void AnOlderWrapStillApplies()
    {
        var older = _bed.MakeWrap(KeyBed.OlderVersion);             // made for the files as they are now
        File.AppendAllText(_bed.SourcePath(1), "update");            // BSG's update changes a resource file ...
        var newer = _bed.MakeWrap(KeyBed.Version);                   // ... and a second wrap is added for it
        _bed.WriteKeyFile(Map, older, newer);

        var (resolver, env) = Make();                                 // the install holds the NEW files
        Assert.True(resolver.TryGetKey(Map, out byte[] key));
        Assert.Equal(KeyBed.Hex(KeyBed.KMap), KeyBed.Hex(key));
        Assert.Contains("built for Arena 0.6.1.0.48206", Assert.Single(env.Debug));

        File.WriteAllBytes(_bed.SourcePath(1), KeyBed.Bytes(KeyBed.Sizes[1], 101)); // a player still on the old build
        var (oldResolver, oldEnv) = Make();
        Assert.True(oldResolver.TryGetKey(Map, out key));
        Assert.Equal(KeyBed.Hex(KeyBed.KMap), KeyBed.Hex(key));
        Assert.Contains("built for Arena 0.6.0.0.47752", Assert.Single(oldEnv.Debug));
        Assert.Empty(oldEnv.Errors);
    }

    [Fact]
    public void WhenNoWrapAppliesTheNewestBuildIsReported()
    {
        var older = _bed.MakeWrap(KeyBed.OlderVersion);
        File.AppendAllText(_bed.SourcePath(1), "update");
        var newer = _bed.MakeWrap("0.6.1.0.48206");
        _bed.WriteKeyFile(Map, older, newer);
        File.AppendAllText(_bed.SourcePath(1), "again"); // an Arena build neither wrap knows

        var (resolver, env) = Make();
        env.VersionValue = "0.7.0.0.50000";
        Assert.False(resolver.TryGetKey(Map, out _));
        Assert.Equal(Head() + "the maps were built for Arena 0.6.1.0.48206 but 0.7.0.0.50000 is installed " +
                     "('EscapeFromTarkovArena_Data/sharedassets41.resource' is 1200011 bytes there, 1200006 expected); a keys update for this Arena build is needed",
            Assert.Single(env.Errors));
    }

    // ---- concurrency and isolation ------------------------------------------------------------------------------

    [Fact]
    public void EightConcurrentCallersCauseOneDerivation()
    {
        _bed.WriteKeyFile();
        var (resolver, env) = Make();
        var keys = new byte[8][];
        using var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            start.Wait();
            Assert.True(resolver.TryGetKey(Map, out keys[i]));
        })).ToList();
        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => Assert.True(t.Join(TimeSpan.FromSeconds(30))));

        Assert.Equal(1, resolver.Derivations);
        Assert.Equal(1, env.OpenFilesCalls);
        Assert.Equal(KeyBed.Sources.Length, env.Files.Opens); // each listed file read once
        Assert.Single(env.Debug);
        Assert.All(keys, k => Assert.Same(keys[0], k));
    }

    [Fact]
    public void ConcurrentFirstRequestsOfDifferentMapsDeriveIndependently()
    {
        _bed.WriteKeyFile();
        _bed.WriteKeyFile(KeyBed.Other, _bed.MakeWrap(map: KeyBed.Other, kMap: KeyBed.OtherKMap));
        var (resolver, env) = Make();
        var maps = new[] { Map, KeyBed.Other };
        var keys = new byte[8][];
        using var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            start.Wait();
            Assert.True(resolver.TryGetKey(maps[i % 2], out keys[i]));
        })).ToList();
        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => Assert.True(t.Join(TimeSpan.FromSeconds(30))));
        Assert.Equal(2, resolver.Derivations);
        Assert.Equal(2, env.Debug.Count);
        Assert.Equal(KeyBed.Hex(KeyBed.KMap), KeyBed.Hex(keys[0]));
        Assert.Equal(KeyBed.Hex(KeyBed.OtherKMap), KeyBed.Hex(keys[1]));
    }

    // ---- the crypto provider warning (W1) -------------------------------------------------------------------------

    [Fact]
    public void W1TheManagedFallbackIsAnnouncedOncePerProcessWithTheBundlesTag()
    {
        _bed.WriteKeyFile();
        _bed.WriteKeyFile(KeyBed.Other, _bed.MakeWrap(map: KeyBed.Other, kMap: KeyBed.OtherKMap));
        var selector = new ProviderSelector(CryptoProviders.Managed, () => throw new PlatformNotSupportedException("no bcrypt"));
        var (resolver, env) = Make();
        env.ProviderFactory = warn => selector.Select(warn);

        Assert.True(resolver.TryGetKey(Map, out _));
        Assert.True(resolver.TryGetKey(KeyBed.Other, out _));
        string line = Assert.Single(env.Warnings);
        Assert.Equal("[Bundles] Windows CNG unavailable (PlatformNotSupportedException: no bcrypt); using managed crypto, which is slow: " +
                     "map loading will take noticeably longer", line);
        Assert.All(env.Debug, d => Assert.EndsWith("crypto: managed)", d));
        Assert.Empty(env.Errors);
    }

    // ---- no key material in any line -----------------------------------------------------------------------------

    [Fact]
    public void NoLineEverContainsAKeyOrAHash()
    {
        var wrap = _bed.MakeWrap();
        _bed.WriteKeyFile(Map, wrap);
        var secrets = new List<string> { KeyBed.Hex(KeyBed.KMap), KeyBed.Hex(wrap.Salt), KeyBed.Hex(wrap.WrappedKey), KeyBed.Hex(wrap.Check) };
        for (int i = 0; i < KeyBed.Sources.Length; i++)
        {
            secrets.Add(KeyBed.Hex(SHA256.HashData(File.ReadAllBytes(_bed.SourcePath(i)))));
            secrets.Add(KeyBed.Hex(_bed.SampleDigest(wrap.Files[i])));   // the digest that enters the key
        }

        var lines = new List<string>();
        var (resolver, env) = Make();
        Assert.True(resolver.TryGetKey(Map, out _));
        lines.AddRange(env.AllLines());

        KeyBed.FlipByte(_bed.SourcePath(0), 3);
        var (flipped, flippedEnv) = Make();
        Assert.False(flipped.TryGetKey(Map, out _));
        lines.AddRange(flippedEnv.AllLines());

        File.AppendAllText(_bed.SourcePath(1), "x");
        var (resized, resizedEnv) = Make();
        Assert.False(resized.TryGetKey(Map, out _));
        lines.AddRange(resizedEnv.AllLines());

        Assert.Equal(3, lines.Count);
        foreach (string line in lines)
            foreach (string secret in secrets)
            {
                Assert.DoesNotContain(secret, line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(secret.Substring(0, 16), line, StringComparison.OrdinalIgnoreCase);
            }
    }
}

public sealed class BundlePrepareTests : IDisposable
{
    const string Map = KeyBed.Map;
    const string Served = "arena_test/arena_test_scenes.bundle";

    readonly KeyBed _bed = new();

    public void Dispose() => _bed.Dispose();

    (BundleKeyResolver resolver, FakeKeyEnvironment env) Make(bool keyFile = true)
    {
        if (keyFile)
            _bed.WriteKeyFile();
        var env = new FakeKeyEnvironment(_bed);
        return (new BundleKeyResolver(env), env);
    }

    [Fact]
    public void AnEncryptedBundleOpensAsASeekableDecryptingStreamOfTheRightLength()
    {
        byte[] plain = KeyBed.Bytes(10_000, 7);
        string path = _bed.WriteBundle("arena_test_scenes.bundle", plain);
        var (resolver, env) = Make();

        using var prepared = resolver.Prepare(path, Map, Served, false);
        Assert.Equal(PreparedKind.Encrypted, prepared.Kind);
        Assert.NotNull(prepared.Stream);
        Assert.Equal(10_000, prepared.Stream.Length);
        Assert.True(prepared.Stream.CanSeek);
        Assert.True(prepared.Stream.CanRead);
        Assert.Equal(plain, KeyBed.ReadAll(prepared.Stream));
        Assert.Equal("arena_test_scenes.bundle", prepared.FileName);
        Assert.Equal(Map, prepared.MapKey);
        Assert.Equal(Served, prepared.ServedKey);
        Assert.Single(env.Debug); // the key derivation line (D1); Prepare itself logs nothing on success
        Assert.Empty(env.Errors);
        Assert.Empty(env.Warnings);

        Assert.Equal("[Bundles] loaded 'arena_test/arena_test_scenes.bundle' from '" + path + "' (10000 bytes plaintext, encrypted AMPB v1, 3 chunks)",
            BundleKeyResolver.LoadedLine(prepared, new FileInfo(path).Length));
    }

    [Fact]
    public void TheNavMeshBundleSaysSoInItsLoadedLine()
    {
        string path = _bed.WriteBundle("arena_test_navmesh.bundle", KeyBed.Bytes(5000, 8));
        var (resolver, _) = Make();
        using var prepared = resolver.Prepare(path, Map, null, true);
        Assert.Equal(PreparedKind.Encrypted, prepared.Kind);
        Assert.Equal("[NavMesh] loaded navmesh bundle for 'arena_test' from '" + path + "' (5000 bytes plaintext, encrypted AMPB v1, 2 chunks)",
            BundleKeyResolver.LoadedLine(prepared, new FileInfo(path).Length));
    }

    [Fact]
    public void ThePreparedStreamIsReadableByManySmallReadsAtRandomOffsets()
    {
        byte[] plain = KeyBed.Bytes(3 * 4096 + 77, 9);
        string path = _bed.WriteBundle("arena_test_scenes.bundle", plain);
        var (resolver, _) = Make();
        using var prepared = resolver.Prepare(path, Map, Served, false);
        var rnd = new Random(5);
        for (int i = 0; i < 200; i++)
        {
            int offset = rnd.Next(0, plain.Length);
            int length = rnd.Next(1, 3000);
            byte[] buffer = new byte[length];
            prepared.Stream.Position = offset;
            int got = prepared.Stream.Read(buffer, 0, length);
            Assert.Equal(Math.Min(length, plain.Length - offset), got);
            Assert.Equal(plain.AsSpan(offset, got).ToArray(), buffer.AsSpan(0, got).ToArray());
        }
    }

    [Fact]
    public void APlaintextBundleLoadsFromItsFileWithoutAnyKey()
    {
        byte[] unityFs = Encoding.ASCII.GetBytes("UnityFS\0").Concat(KeyBed.Bytes(300, 4)).ToArray();
        string path = Path.Combine(_bed.Bundles, "arena_test_preset.bundle");
        File.WriteAllBytes(path, unityFs);
        var (resolver, env) = Make(keyFile: false); // there is not even a key file

        using var prepared = resolver.Prepare(path, Map, "maps/arena_test_preset.bundle", false);
        Assert.Equal(PreparedKind.Plaintext, prepared.Kind);
        Assert.Null(prepared.Stream);
        Assert.Equal(0, env.EnsureCalls);
        Assert.Equal(0, resolver.Derivations);
        Assert.Empty(env.AllLines());
        Assert.Equal("[Bundles] loaded 'maps/arena_test_preset.bundle' from '" + path + "' (308 bytes, plaintext bundle, not encrypted)",
            BundleKeyResolver.LoadedLine(prepared, 308));
        File.Delete(path); // the file handle is already released
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(500)]
    public void AnythingWithoutTheMagicIsLeftToUnityAsAPlaintextBundle(int length)
    {
        string path = Path.Combine(_bed.Bundles, "other.bundle");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)7, length).ToArray());
        var (resolver, env) = Make(keyFile: false);
        using var prepared = resolver.Prepare(path, Map, Served, false);
        Assert.Equal(PreparedKind.Plaintext, prepared.Kind);
        Assert.Empty(env.AllLines());
    }

    [Fact]
    public void WithoutAKeyTheBundleIsRefusedWithTheCauseOnceAndNoKeyPerBundle()
    {
        string scenes = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        string navmesh = _bed.WriteBundle("arena_test_navmesh.bundle", KeyBed.Bytes(5000, 8));
        var (resolver, env) = Make(keyFile: false); // E1: no key file

        var first = resolver.Prepare(scenes, Map, Served, false);
        var second = resolver.Prepare(navmesh, Map, null, true);
        Assert.Equal(PreparedKind.Refused, first.Kind);
        Assert.Equal(PreparedKind.Refused, second.Kind);
        Assert.Null(first.Stream);

        // the cause once (from the first request), then only "no key" per refused bundle
        string[] errors = env.Errors.ToArray();
        Assert.Equal(3, errors.Length);
        Assert.StartsWith("[BundleKeys] key for 'arena_test' unavailable: key file '", errors[0]);
        Assert.Equal("[Bundles] 'arena_test/arena_test_scenes.bundle' not served: no key for 'arena_test' (see the line above)", errors[1]);
        Assert.Equal("[NavMesh] navmesh bundle for 'arena_test' not loaded: no key (see the line above)", errors[2]);
        Assert.Equal(BundleKeyResolver.NoKeyLine(Map, Served, false), errors[1]);
        Assert.Equal(BundleKeyResolver.NoKeyLine(Map, null, true), errors[2]);
        Assert.Equal(1, resolver.Derivations); // the second request did not derive again
        File.Delete(scenes); // refused bundles do not keep their files open
        File.Delete(navmesh);
    }

    [Fact]
    public void WithoutAVerifiedArenaTheBundleIsRefusedWithoutReadingAnything()
    {
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        var (resolver, env) = Make();
        env.Verified = false;
        using var prepared = resolver.Prepare(path, Map, Served, false);
        Assert.Equal(PreparedKind.Refused, prepared.Kind);
        Assert.Equal(0, env.OpenFilesCalls);
        Assert.Equal("[Bundles] 'arena_test/arena_test_scenes.bundle' not served: no key for 'arena_test' (see the line above)", Assert.Single(env.Errors));
        Assert.Empty(env.Debug);
    }

    [Fact]
    public void ABundleOfAnotherKeySetFailsItsHeaderAuthentication()
    {
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7), kMap: KeyBed.OtherKMap);
        var (resolver, env) = Make();
        var prepared = resolver.Prepare(path, Map, Served, false);
        Assert.Equal(PreparedKind.Refused, prepared.Kind);
        string line = Assert.Single(env.Errors);
        Assert.StartsWith("[Bundles] 'arena_test/arena_test_scenes.bundle' not served: 'arena_test_scenes.bundle' is not a valid map bundle (header authentication failed", line);
        Assert.EndsWith(")", line);
        File.Delete(path); // the failed constructor closed the file
    }

    [Fact]
    public void ATruncatedBundleIsNotAValidMapBundle()
    {
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(9000, 7));
        KeyBed.Truncate(path, 10);
        var (resolver, env) = Make();
        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);
        Assert.StartsWith("[Bundles] 'arena_test/arena_test_scenes.bundle' not served: 'arena_test_scenes.bundle' is not a valid map bundle (truncated", Assert.Single(env.Errors));
    }

    [Fact]
    public void AContainerVersionThisClientDoesNotKnowIsNotAValidMapBundle()
    {
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(9000, 7));
        KeyBed.FlipByte(path, 4);
        var (resolver, env) = Make();
        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);
        Assert.Contains("is not a valid map bundle (unsupported container version", Assert.Single(env.Errors));
    }

    [Fact]
    public void ABundleOfAnotherMapIsRefusedByItsHeader()
    {
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7), map: KeyBed.Other);
        var (resolver, env) = Make();
        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);
        Assert.Equal("[Bundles] 'arena_test/arena_test_scenes.bundle' not served: 'arena_test_scenes.bundle' is not a valid map bundle " +
                     "(the container belongs to 'arena_other', not to 'arena_test')", Assert.Single(env.Errors));
    }

    [Fact]
    public void TheNavMeshBundleHasItsOwnRefusalWording()
    {
        string path = _bed.WriteBundle("arena_test_navmesh.bundle", KeyBed.Bytes(5000, 7), kMap: KeyBed.OtherKMap);
        var (resolver, env) = Make();
        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, null, true).Kind);
        string line = Assert.Single(env.Errors);
        Assert.StartsWith("[NavMesh] navmesh bundle for 'arena_test' not loaded: 'arena_test_navmesh.bundle' is not a valid map bundle (header authentication failed", line);
    }

    [Fact]
    public void AMissingBundleFileIsRefusedWithoutAnException()
    {
        var (resolver, env) = Make();
        var prepared = resolver.Prepare(Path.Combine(_bed.Bundles, "missing.bundle"), Map, Served, false);
        Assert.Equal(PreparedKind.Refused, prepared.Kind);
        string line = Assert.Single(env.Errors);
        Assert.StartsWith("[Bundles] 'arena_test/arena_test_scenes.bundle' not served: 'missing.bundle' cannot be read (", line);
        Assert.Equal(0, env.EnsureCalls);
    }

    [Fact]
    public void ABundleThatIsLockedIsRefusedWithoutAnException()
    {
        if (!OperatingSystem.IsWindows())
            return;
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        var (resolver, env) = Make();
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);
        Assert.StartsWith("[Bundles] 'arena_test/arena_test_scenes.bundle' not served: 'arena_test_scenes.bundle' cannot be read (", Assert.Single(env.Errors));
    }

    [Fact]
    public void DisposingAPreparedBundleClosesTheFileAndMayBeRepeated()
    {
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        var (resolver, _) = Make();
        var prepared = resolver.Prepare(path, Map, Served, false);
        Assert.Equal(PreparedKind.Encrypted, prepared.Kind);
        prepared.Dispose();
        prepared.Dispose();
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void EveryLineIsLoggedOnTheThreadThatCalledNeverOnAThreadOfItsOwn()
    {
        string scenes = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(9000, 7));
        string other = _bed.WriteBundle("arena_other_scenes.bundle", KeyBed.Bytes(5000, 8), map: KeyBed.Other);
        var (resolver, env) = Make();
        int caller = 0;
        var thread = new Thread(() =>
        {
            caller = Environment.CurrentManagedThreadId;
            var prepared = resolver.Prepare(scenes, Map, Served, false);                    // D1, then a clean open
            resolver.Streams.Track(prepared, () => true);
            KeyBed.ReadAll(prepared.Stream);
            resolver.Streams.LogStats(Map, "raid start");                                   // D3
            resolver.Prepare(other, KeyBed.Other, "arena_other/arena_other_scenes.bundle", false); // E1 (no key file) and E9
            resolver.Streams.Track(prepared, () => false);                                  // a second entry that EFT "unloaded"
            resolver.Streams.Maintain();                                                    // D3 for the unloaded bundle
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));

        Assert.True(env.LogThreads.Count >= 5, "expected D1, D3, E1, E9, D3 at least");
        Assert.All(env.LogThreads, id => Assert.Equal(caller, id));
        Assert.NotEqual(Environment.CurrentManagedThreadId, caller);
    }

    [Fact]
    public void TheDefaultResolverIsOneObjectAndStartsEmpty()
    {
        Assert.Same(BundleKeyResolver.Default, BundleKeyResolver.Default);
        Assert.Equal(0, BundleKeyResolver.Default.Derivations);
        Assert.Equal(0, BundleKeyResolver.Default.Streams.Count);
    }
}

public sealed class BundleStreamRegistryTests : IDisposable
{
    const string Map = KeyBed.Map;
    const string Served = "arena_test/arena_test_scenes.bundle";

    readonly KeyBed _bed = new();

    public void Dispose() => _bed.Dispose();

    (BundleKeyResolver resolver, FakeKeyEnvironment env) Make()
    {
        _bed.WriteKeyFile();
        var env = new FakeKeyEnvironment(_bed);
        return (new BundleKeyResolver(env), env);
    }

    PreparedBundle Open(BundleKeyResolver resolver, string fileName, byte[] plain, int chunkSize = 4096, bool navMesh = false)
    {
        string path = _bed.WriteBundle(fileName, plain, chunkSize);
        var prepared = resolver.Prepare(path, Map, navMesh ? null : Served, navMesh);
        Assert.Equal(PreparedKind.Encrypted, prepared.Kind);
        return prepared;
    }

    [Fact]
    public void ABundleThatEFTUnloadedHasItsStreamClosedAndItsCountersLogged()
    {
        var (resolver, env) = Make();
        var prepared = Open(resolver, "arena_test_scenes.bundle", KeyBed.Bytes(3 << 20, 3), 1 << 20);
        KeyBed.ReadAll(prepared.Stream);
        bool alive = true;
        resolver.Streams.Track(prepared, () => alive);
        Assert.Equal(1, resolver.Streams.Count);

        resolver.Streams.Maintain();
        Assert.Equal(1, resolver.Streams.Count);        // still loaded: the stream stays, across raids
        prepared.Stream.Position = 0;
        Assert.True(prepared.Stream.ReadByte() >= 0);   // and stays readable

        alive = false;                                    // EFT unloaded the bundle
        env.Debug.Clear();
        resolver.Streams.Maintain();
        Assert.Equal(0, resolver.Streams.Count);
        Assert.Throws<ObjectDisposedException>(() => prepared.Stream.ReadByte());
        string line = Assert.Single(env.Debug);
        Assert.Matches(new Regex(@"^\[Bundles\] stream 'arena_test_scenes\.bundle' \(bundle unloaded\): 3\.0 MiB decrypted in 3 chunks, cache hits \d+ / misses 3, crypto \d+ ms$"), line);
    }

    [Fact]
    public void AProbeThatThrowsCountsAsUnloaded()
    {
        var (resolver, _) = Make();
        var prepared = Open(resolver, "arena_test_scenes.bundle", KeyBed.Bytes(5000, 3));
        resolver.Streams.Track(prepared, () => throw new InvalidOperationException("probe"));
        resolver.Streams.Maintain();
        Assert.Equal(0, resolver.Streams.Count);
        Assert.Throws<ObjectDisposedException>(() => prepared.Stream.ReadByte());
    }

    [Fact]
    public void APlaintextOrRefusedBundleIsNotTracked()
    {
        var (resolver, _) = Make();
        resolver.Streams.Track(null, () => true);
        resolver.Streams.Track(new PreparedBundle(PreparedKind.Plaintext, "x.bundle", Map, Served, false, null), () => true);
        resolver.Streams.Track(new PreparedBundle(PreparedKind.Refused, "x.bundle", Map, Served, false, null), () => true);
        Assert.Equal(0, resolver.Streams.Count);
    }

    [Fact]
    public void TheCountersLineReportsTheDecryptedBytesChunksAndCacheUse()
    {
        var (resolver, env) = Make();
        var prepared = Open(resolver, "arena_test_scenes.bundle", KeyBed.Bytes(3 << 20, 3), 1 << 20);
        var buffer = new byte[3 << 20];
        Assert.Equal(3 << 20, prepared.Stream.Read(buffer, 0, buffer.Length)); // one read over three chunks: 3 misses, no hit
        resolver.Streams.Track(prepared, () => true);
        env.Debug.Clear();

        resolver.Streams.LogStats(Map, "raid start");
        Assert.Matches(new Regex(@"^\[Bundles\] stream 'arena_test_scenes\.bundle' \(raid start\): 3\.0 MiB decrypted in 3 chunks, cache hits 0 / misses 3, crypto \d+ ms$"),
            Assert.Single(env.Debug));

        env.Debug.Clear();
        prepared.Stream.Position = 0;
        Assert.Equal(1000, prepared.Stream.Read(buffer, 0, 1000)); // chunk 0 again: a cache hit
        resolver.Streams.LogStats(Map, "scene unload");
        Assert.Matches(new Regex(@"^\[Bundles\] stream 'arena_test_scenes\.bundle' \(scene unload\): 3\.0 MiB decrypted in 3 chunks, cache hits 1 / misses 3, crypto \d+ ms$"),
            Assert.Single(env.Debug));
    }

    [Fact]
    public void TheCountersOfOtherMapsAreNotLogged()
    {
        var (resolver, env) = Make();
        var mine = Open(resolver, "arena_test_scenes.bundle", KeyBed.Bytes(5000, 3));
        string otherPath = _bed.WriteBundle("arena_other_scenes.bundle", KeyBed.Bytes(5000, 4), 4096, KeyBed.KMap, KeyBed.Other);
        var theirs = new PreparedBundle(PreparedKind.Encrypted, otherPath, KeyBed.Other, "x", false,
            new AmpbReadStream(File.OpenRead(otherPath), KeyBed.Other, KeyBed.KMap, CryptoProviders.Managed));
        resolver.Streams.Track(mine, () => true);
        resolver.Streams.Track(theirs, () => true);
        env.Debug.Clear();
        resolver.Streams.LogStats(Map, "raid start");
        Assert.Contains("'arena_test_scenes.bundle'", Assert.Single(env.Debug));
        theirs.Dispose();
    }

    [Fact]
    public void ADamagedChunkIsReportedOnceWithItsIndexAndOffset()
    {
        var (resolver, env) = Make();
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(3 * 4096, 3), 4096);
        long payload = Ampb.Inspect(path).Header.PayloadOffset;
        KeyBed.FlipByte(path, payload + 4096 + 10); // inside chunk 1
        var prepared = resolver.Prepare(path, Map, Served, false);
        Assert.Equal(PreparedKind.Encrypted, prepared.Kind);
        resolver.Streams.Track(prepared, () => true);

        prepared.Stream.Position = 0;
        Assert.Equal(4096, prepared.Stream.Read(new byte[4096], 0, 4096)); // chunk 0 is fine
        Assert.Empty(env.Errors);
        prepared.Stream.Position = 4096;
        var thrown = Assert.Throws<AmpbException>(() => prepared.Stream.Read(new byte[100], 0, 100));
        Assert.Equal(AmpbError.ChunkIntegrity, thrown.Kind);
        Assert.Equal(1, thrown.Chunk);

        resolver.Streams.Maintain();
        resolver.Streams.Maintain();
        resolver.Streams.ReportError(prepared);
        Assert.Equal("[Bundles] 'arena_test_scenes.bundle' failed its integrity check at chunk 1 (offset 4096); the file is damaged, reinstall ArenaMapPort",
            Assert.Single(env.Errors));
    }

    [Fact]
    public void TheNavMeshStreamReportsADamagedChunkWithTheNavMeshTag()
    {
        var (resolver, env) = Make();
        string path = _bed.WriteBundle("arena_test_navmesh.bundle", KeyBed.Bytes(2 * 4096, 3), 4096);
        KeyBed.FlipByte(path, Ampb.Inspect(path).Header.PayloadOffset + 5); // inside chunk 0
        var prepared = resolver.Prepare(path, Map, null, true);
        Assert.Equal(PreparedKind.Encrypted, prepared.Kind);
        Assert.Throws<AmpbException>(() => prepared.Stream.Read(new byte[10], 0, 10));
        resolver.Streams.ReportError(prepared);
        Assert.Equal("[NavMesh] 'arena_test_navmesh.bundle' failed its integrity check at chunk 0 (offset 0); the file is damaged, reinstall ArenaMapPort",
            Assert.Single(env.Errors));
        prepared.Dispose();
    }

    [Fact]
    public void AStreamThatReadCleanlyReportsNothing()
    {
        var (resolver, env) = Make();
        var prepared = Open(resolver, "arena_test_scenes.bundle", KeyBed.Bytes(9000, 3));
        KeyBed.ReadAll(prepared.Stream);
        resolver.Streams.Track(prepared, () => true);
        resolver.Streams.Maintain();
        resolver.Streams.ReportError(prepared);
        resolver.Streams.ReportError(null);
        Assert.Empty(env.Errors);
    }

    [Fact]
    public void AnErrorOfADisposedStreamDoesNotThrow()
    {
        var (resolver, env) = Make();
        var prepared = Open(resolver, "arena_test_scenes.bundle", KeyBed.Bytes(9000, 3));
        prepared.Dispose();
        resolver.Streams.ReportError(prepared);
        Assert.Empty(env.Errors);
    }

    [Fact]
    public void MaintainingWhileAnotherThreadReadsIsSafe()
    {
        var (resolver, env) = Make();
        byte[] plain = KeyBed.Bytes(64 * 4096, 3);
        var prepared = Open(resolver, "arena_test_scenes.bundle", plain);
        resolver.Streams.Track(prepared, () => true);
        using var stop = new ManualResetEventSlim(false);
        Exception failure = null;
        var reader = new Thread(() =>
        {
            try
            {
                var rnd = new Random(1);
                var buffer = new byte[3000];
                while (!stop.IsSet)
                {
                    int offset = rnd.Next(0, plain.Length - 3000);
                    int got = prepared.Stream.ReadAt(offset, buffer, 0, 3000);
                    if (got != 3000 || !plain.AsSpan(offset, 3000).SequenceEqual(buffer))
                        throw new InvalidOperationException("wrong bytes at " + offset);
                }
            }
            catch (Exception e) { failure = e; }
        });
        reader.Start();
        for (int i = 0; i < 200; i++)
        {
            resolver.Streams.Maintain();
            resolver.Streams.LogStats(Map, "test");
        }
        stop.Set();
        Assert.True(reader.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(failure);
        Assert.Empty(env.Errors);
    }
}

/// <summary>The real environment against the real stage-1 gate (state is process-wide, so this class shares the lifecycle collection).</summary>
[Collection("MapPort client lifecycle")]
public sealed class BundleKeyEnvironmentGateTests : IDisposable
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public BundleKeyEnvironmentGateTests() => ClientGate.ResetForTests();

    public void Dispose() => ClientGate.ResetForTests();

    static ArenaInstallationResult Found(string path, string version = "0.6.1.0.48206") =>
        new(true, path, "registry", version, "0.6.0.0.47752", 15351, 76_100_000_000L, null, 0, null);

    static ArenaInstallationResult Missing() =>
        new(false, @"D:\Not Verified", "none", null, null, 0, 0, "No Escape from Tarkov: Arena installation is registered.", 0, null);

    [Fact]
    public void TheEnvironmentReadsTheInstallFolderAndVersionOfTheVerifiedGate()
    {
        var env = new PluginBundleKeyEnvironment();
        ClientGate.Start("", _ => Found(@"D:\Synthetic Arena"));
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.True(env.EnsureVerified());
        Assert.Equal(@"D:\Synthetic Arena", env.InstallPath);
        Assert.Equal("0.6.1.0.48206", env.InstalledVersion);
        Assert.EndsWith(Path.DirectorySeparatorChar + "keys", env.KeysDirectory);
    }

    [Fact]
    public void AnUnverifiedGateYieldsNoInstallFolder()
    {
        var env = new PluginBundleKeyEnvironment();
        ClientGate.Start("", _ => Missing());
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.False(env.EnsureVerified());
        Assert.Null(env.InstallPath); // the folder of a failed check is not an installation
    }

    [Fact]
    public void AGateThatNeverRanIsNotVerified()
    {
        var env = new PluginBundleKeyEnvironment();
        Assert.False(env.EnsureVerified());
        Assert.Null(env.InstallPath);
        Assert.Null(env.InstalledVersion);
    }

    [Fact]
    public void TheResolverReadsNothingWhileTheRealGateIsNotVerified()
    {
        using var bed = new KeyBed();
        bed.WriteKeyFile();
        var env = new GateBackedEnvironment(bed);
        var resolver = new BundleKeyResolver(env);
        ClientGate.Start("", _ => Missing());
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.False(resolver.TryGetKey(KeyBed.Map, out _));
        Assert.Null(env.Files); // no file access was even created
        Assert.Equal(0, resolver.Derivations);

        ClientGate.Start("", _ => Found(bed.Install)); // a later check verifies Arena: the same resolver derives now
        Assert.True(ClientGate.WaitForResult(Patience));
        Assert.True(resolver.TryGetKey(KeyBed.Map, out byte[] key));
        Assert.Equal(KeyBed.Hex(KeyBed.KMap), KeyBed.Hex(key));
        Assert.Equal(KeyBed.Sources.Length, env.Files.Opens);
    }

    /// <summary>The real gate and the real install-path and version properties, with the keys folder and the file access of a <see cref="KeyBed"/>.</summary>
    sealed class GateBackedEnvironment : IBundleKeyEnvironment
    {
        readonly PluginBundleKeyEnvironment _real = new();
        readonly KeyBed _bed;
        internal CountingArenaFiles Files;

        internal GateBackedEnvironment(KeyBed bed) => _bed = bed;

        public bool EnsureVerified() => _real.EnsureVerified();
        public string InstallPath => _real.InstallPath;
        public string InstalledVersion => _real.InstalledVersion;
        public string KeysDirectory => _bed.Keys;
        public IArenaFiles OpenArenaFiles(string installPath) => Files = new CountingArenaFiles(new DirectoryArenaFiles(installPath));
        public ICryptoProvider CryptoProvider(Action<string> warning) => _real.CryptoProvider(warning);
        public void LogDebug(string message) { }
        public void LogWarning(string message) { }
        public void LogError(string message) { }
    }
}

/// <summary>Source-level guards for what the loaders must do (they call Unity and cannot run here): one loader, the buffer argument, the order of the steps.</summary>
public sealed class BundleLoaderSourceTests
{
    static string RepoFile(string relative)
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, relative);
                if (File.Exists(path)) return path;
            }
        }
        throw new FileNotFoundException(relative);
    }

    static string Text(string relative) => File.ReadAllText(RepoFile(relative));

    static IEnumerable<string> ClientSources()
    {
        string clientDir = Path.GetDirectoryName(RepoFile("ArenaMapPort.Client/BundleHost.cs"));
        return Directory.EnumerateFiles(clientDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void OnlyBundleHostCallsAnAssetBundleLoader()
    {
        var loader = new Regex(@"AssetBundle\s*\.\s*LoadFrom\w*\s*\(");
        foreach (string file in ClientSources())
        {
            string name = Path.GetFileName(file);
            if (name == "BundleHost.cs")
                continue;
            Assert.False(loader.IsMatch(File.ReadAllText(file)), name + " loads an AssetBundle itself; BundleHost.LoadAssetBundle is the one loader");
        }
        string host = Text("ArenaMapPort.Client/BundleHost.cs");
        Assert.Single(Regex.Matches(host, @"AssetBundle\.LoadFromFile\("));
        Assert.Single(Regex.Matches(host, @"AssetBundle\.LoadFromStream\("));
    }

    [Fact]
    public void LoadFromStreamAlwaysPassesTheOneChunkReadBuffer()
    {
        string host = Text("ArenaMapPort.Client/BundleHost.cs");
        Assert.Matches(new Regex(@"AssetBundle\.LoadFromStream\(prepared\.Stream, 0u, BundleKeyResolver\.ReadBufferSize\)"), host);
        Assert.Equal(1u << 20, BundleKeyResolver.ReadBufferSize);
        // the buffer is one container chunk (D13): a read of the managed buffer never straddles more than two chunks
        Assert.Equal(Ampb.DefaultChunkSize, (int)BundleKeyResolver.ReadBufferSize);
    }

    [Fact]
    public void TheStreamPositionIsResetBeforeUnityTakesItAsTheBundleStart()
    {
        string host = Text("ArenaMapPort.Client/BundleHost.cs");
        int reset = host.IndexOf("prepared.Stream.Position = 0;", StringComparison.Ordinal);
        int load = host.IndexOf("AssetBundle.LoadFromStream(", StringComparison.Ordinal);
        Assert.True(reset > 0 && load > reset);
    }

    [Fact]
    public void TryServeKeepsTheGateFirstThenHousekeepingThenTheLoadThenTracksTheStream()
    {
        string host = Text("ArenaMapPort.Client/BundleHost.cs");
        int serve = host.IndexOf("internal static void TryServe", StringComparison.Ordinal);
        int gate = host.IndexOf("ArenaInstallationGate.EnsureVerified()", serve, StringComparison.Ordinal);
        int maintain = host.IndexOf("MaintainStreams();", serve, StringComparison.Ordinal);
        int cached = host.IndexOf("manager._bundles.ContainsKey(bundleName)", serve, StringComparison.Ordinal);
        int exists = host.IndexOf("File.Exists(filePath)", serve, StringComparison.Ordinal);
        int load = host.IndexOf("LoadAssetBundle(filePath, mapKey, bundleName, false, out var prepared)", serve, StringComparison.Ordinal);
        int register = host.IndexOf("manager._bundles[bundleName] = reference;", serve, StringComparison.Ordinal);
        int track = host.IndexOf("BundleKeyResolver.Default.Streams.Track(prepared", serve, StringComparison.Ordinal);
        Assert.True(gate > serve && maintain > gate && cached > maintain && exists > cached && load > exists && register > load && track > register,
            "gate, housekeeping, cached check, file check, load, registration, tracking");
        // a failed load returns before anything is registered
        Assert.Matches(new Regex(@"out var prepared\);\s*if \(bundle == null\)\s*\{\s*return;\s*\}"), host);
    }

    [Fact]
    public void TheStreamOfALoadedBundleIsOnlyClosedOnceEFTHasUnloadedIt()
    {
        string host = Text("ArenaMapPort.Client/BundleHost.cs");
        // alive = EFT's table holds this very instance; never "the scene unloaded"
        Assert.Contains("manager._bundles.TryGetValue(bundleName, out var reference) &&", host);
        Assert.Contains("ReferenceEquals(reference.AssetBundle, bundle)", host);
        string nav = Text("ArenaMapPort.Client/NavMeshInstaller.cs");
        int scene = nav.IndexOf("static void OnSceneUnloaded", StringComparison.Ordinal);
        int end = nav.IndexOf("static void Drop", scene, StringComparison.Ordinal);
        string unloadHandler = nav.Substring(scene, end - scene);
        Assert.Contains("BundleHost.LogStreamStats(map.Key, \"scene unload\")", unloadHandler);
        Assert.DoesNotContain("Dispose", unloadHandler);
    }

    [Fact]
    public void TheNavMeshInstallerLoadsThroughTheHostAndClosesItsStreamAfterUnload()
    {
        string nav = Text("ArenaMapPort.Client/NavMeshInstaller.cs");
        int once = nav.IndexOf("static NavMeshData LoadOnce", StringComparison.Ordinal);
        int gate = nav.IndexOf("ArenaInstallationGate.EnsureVerified()", once, StringComparison.Ordinal);
        int exists = nav.IndexOf("File.Exists(path)", once, StringComparison.Ordinal);
        int load = nav.IndexOf("BundleHost.LoadAssetBundle(path, map.Key, null, true, out prepared)", once, StringComparison.Ordinal);
        int assets = nav.IndexOf("bundle.LoadAllAssets(typeof(NavMeshData))", once, StringComparison.Ordinal);
        int unload = nav.IndexOf("bundle?.Unload(false);", once, StringComparison.Ordinal);
        int close = nav.IndexOf("CloseStream(prepared);", once, StringComparison.Ordinal);
        Assert.True(gate > once && exists > gate && load > exists && assets > load && unload > assets && close > unload,
            "gate, file check, load, assets, Unload(false), then close the stream");
        Assert.Contains("_loadTried[map.Key] = true;", nav);
        Assert.Contains("BundleHost.LogStreamStats(map.Key, \"raid start\")", nav);
        Assert.Contains("BundleStreamRegistry.StatsLine(prepared, \"navmesh load\")", nav);
    }

    [Fact]
    public void TheResolverNeverTouchesUnityAndNeverConvertsAKeyToText()
    {
        string source = Text("ArenaMapPort.Client/BundleKeyResolver.cs");
        Assert.DoesNotContain("using UnityEngine", source);
        Assert.DoesNotContain("HexCodec", source);
        Assert.DoesNotContain("ToHexString", source);
        var logCalls = Regex.Matches(source, @"_env\.Log(Debug|Warning|Error)\(([^;]*)\);");
        Assert.True(logCalls.Count >= 8);
        foreach (Match log in logCalls)
            Assert.DoesNotContain(".MapKey", log.Value); // the content key is never an argument of a log call
    }

    [Fact]
    public void EveryLogTagIsOneOfTheThree()
    {
        Assert.Equal("[BundleKeys]", BundleKeyResolver.KeyTag);
        Assert.Equal("[Bundles]", BundleKeyResolver.BundlesTag);
        Assert.Equal("[NavMesh]", BundleKeyResolver.NavMeshTag);
        Assert.Equal("keys", BundleKeyResolver.KeysFolder);
        Assert.Equal(16, BundleKeyResolver.CacheSlots);
    }
}
