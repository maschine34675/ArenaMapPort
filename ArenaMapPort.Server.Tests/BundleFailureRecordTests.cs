#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using Xunit;

namespace ArenaMapPort.Server.Tests;

// What BundleKeyResolver.Prepare and the stream registry leave in the failure registry for the in-game map notices (docs/progress/map-notices.md):
// one record per refusal with the cause, one more for every later refusal of the same map (the cached key failure records again per attempt), NavMesh
// problems as a NavMesh cause that never blocks, damaged chunks as Integrity, and not a single changed log line (the E1-E9 tests stay as they are).

public sealed class BundleFailureRecordTests : IDisposable
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

    static MapLoadFailure Only(BundleKeyResolver resolver)
    {
        IReadOnlyList<MapLoadFailure> taken = resolver.Failures.TakeBlocking(Map, 0, resolver.Failures.CurrentSequence);
        return Assert.Single(taken);
    }

    // ---- key failures ---------------------------------------------------------------------------------------------

    [Fact]
    public void ARefusalForWantOfAKeyFileRecordsTheKeyCauseAndTheKeyFileName()
    {
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        var (resolver, env) = Make(keyFile: false);

        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);

        MapLoadFailure record = Only(resolver);
        Assert.Equal(MapLoadFailureKind.Key, record.Kind);
        Assert.Equal(KeyFailure.KeyFileMissing, record.KeyCause);
        Assert.Equal(Map, record.MapKey);
        Assert.Equal("arena_test.json", record.File);
        Assert.False(string.IsNullOrEmpty(record.Detail));
        Assert.Equal(KeyBed.Version, record.InstalledVersion);
        Assert.Equal(2, env.Errors.Count); // the cause and "no key": unchanged
    }

    [Fact]
    public void AnArenaOfAnotherBuildRecordsBothVersionsAndTheFileThatDiffers()
    {
        _bed.WriteKeyFile(Map, _bed.MakeWrap(builtFor: KeyBed.OlderVersion));
        File.AppendAllText(_bed.SourcePath(0), "grown"); // another build: the first file has another size
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        var env = new FakeKeyEnvironment(_bed);
        var resolver = new BundleKeyResolver(env);

        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);

        MapLoadFailure record = Only(resolver);
        Assert.Equal(KeyFailure.SizeDiffers, record.KeyCause);
        Assert.Equal(KeyBed.OlderVersion, record.BuiltFor);
        Assert.Equal(KeyBed.Version, record.InstalledVersion);
        Assert.Equal(KeyBed.Sources[0], record.FailedPath);
    }

    [Fact]
    public void FilesWithTheListedSizesThatDoNotUnlockTheKeyRecordContentDiffers()
    {
        _bed.WriteKeyFile();
        KeyBed.FlipByte(_bed.SourcePath(0), 3);
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        var resolver = new BundleKeyResolver(new FakeKeyEnvironment(_bed));

        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);

        MapLoadFailure record = Only(resolver);
        Assert.Equal(MapLoadFailureKind.Key, record.Kind);
        Assert.Equal(KeyFailure.ContentDiffers, record.KeyCause);
        Assert.Equal(KeyBed.Version, record.BuiltFor);
    }

    [Fact]
    public void AnArenaFileThatCannotBeReadRecordsItsCause()
    {
        _bed.WriteKeyFile();
        File.Delete(_bed.SourcePath(1));
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        var resolver = new BundleKeyResolver(new FakeKeyEnvironment(_bed));

        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);

        Assert.Equal(KeyFailure.ArenaFileUnreadable, Only(resolver).KeyCause);
    }

    [Fact]
    public void ADerivationThatThrowsRecordsTheInternalCause()
    {
        _bed.WriteKeyFile();
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        var env = new FakeKeyEnvironment(_bed) { ProviderFactory = _ => throw new PlatformNotSupportedException("no crypto") };
        var resolver = new BundleKeyResolver(env);

        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);

        MapLoadFailure record = Only(resolver);
        Assert.Equal(KeyFailure.Internal, record.KeyCause);
        Assert.Contains("PlatformNotSupportedException", record.Detail);
        Assert.Equal("arena_test.json", record.File);
    }

    [Fact]
    public void TheCachedKeyFailureIsRecordedAgainForEveryAttemptWithoutDerivingAgain()
    {
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        var (resolver, env) = Make(keyFile: false);

        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);
        long afterFirst = resolver.Failures.CurrentSequence;
        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);
        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);

        Assert.Equal(1, afterFirst);
        Assert.Equal(3, resolver.Failures.CurrentSequence);
        Assert.Equal(1, resolver.Derivations);
        IReadOnlyList<MapLoadFailure> second = resolver.Failures.TakeBlocking(Map, afterFirst, resolver.Failures.CurrentSequence);
        Assert.Equal(2, second.Count);
        Assert.All(second, record => Assert.Equal(KeyFailure.KeyFileMissing, record.KeyCause)); // the cause survives the cache
        Assert.Equal(1 + 3, env.Errors.Count); // one cause line, one "no key" line per refused bundle (the existing behaviour)
    }

    [Fact]
    public void WithoutAVerifiedArenaTheRefusalIsAGateRefusalAndNotACachedKeyFailure()
    {
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        var (resolver, env) = Make();
        env.Verified = false;

        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);

        MapLoadFailure record = Only(resolver);
        Assert.Equal(MapLoadFailureKind.GateRefused, record.Kind);
        Assert.Equal("arena_test_scenes.bundle", record.File);

        // a check that verifies later still opens the bundle: the refusal was not cached
        env.Verified = true;
        using var prepared = resolver.Prepare(path, Map, Served, false);
        Assert.Equal(PreparedKind.Encrypted, prepared.Kind);
        Assert.Equal(0, resolver.Failures.Count);
    }

    // ---- bundle problems --------------------------------------------------------------------------------------------

    [Fact]
    public void ABundleOfAnotherKeySetIsRecordedAsInvalidWithItsFileName()
    {
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7), kMap: KeyBed.OtherKMap);
        var (resolver, _) = Make();

        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);

        MapLoadFailure record = Only(resolver);
        Assert.Equal(MapLoadFailureKind.BundleInvalid, record.Kind);
        Assert.Equal("arena_test_scenes.bundle", record.File);
        Assert.Contains("header authentication failed", record.Detail);
    }

    [Fact]
    public void ATruncatedBundleIsRecordedAsInvalid()
    {
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(9000, 7));
        KeyBed.Truncate(path, 10);
        var (resolver, _) = Make();

        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);

        Assert.Equal(MapLoadFailureKind.BundleInvalid, Only(resolver).Kind);
    }

    [Fact]
    public void AMissingBundleFileIsRecordedAsMissing()
    {
        var (resolver, _) = Make();

        Assert.Equal(PreparedKind.Refused, resolver.Prepare(Path.Combine(_bed.Bundles, "missing.bundle"), Map, Served, false).Kind);

        MapLoadFailure record = Only(resolver);
        Assert.Equal(MapLoadFailureKind.BundleMissing, record.Kind);
        Assert.Equal("missing.bundle", record.File);
    }

    [Fact]
    public void ALockedBundleIsRecordedAsUnreadable()
    {
        if (!OperatingSystem.IsWindows())
            return;
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        var (resolver, _) = Make();

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);

        Assert.Equal(MapLoadFailureKind.BundleUnreadable, Only(resolver).Kind);
    }

    [Fact]
    public void ASuccessfulOpenAndAPlaintextBundleRecordNothing()
    {
        string encrypted = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        string plain = Path.Combine(_bed.Bundles, "arena_test_preset.bundle");
        File.WriteAllBytes(plain, new byte[] { 0x55, 0x6E, 0x69, 0x74, 0x79, 0x46, 0x53, 0, 1, 2 });
        var (resolver, _) = Make();

        using (resolver.Prepare(encrypted, Map, Served, false)) { }
        using (resolver.Prepare(plain, Map, "arena_test/arena_test_preset.bundle", false)) { }

        Assert.Equal(0, resolver.Failures.CurrentSequence);
        Assert.Equal(0, resolver.Failures.Count);
    }

    // ---- the NavMesh bundle is never blocking ----------------------------------------------------------------------------

    [Fact]
    public void ANavMeshBundleWithoutAKeyIsANavMeshCauseThatNeverBlocks()
    {
        string path = _bed.WriteBundle("arena_test_navmesh.bundle", KeyBed.Bytes(5000, 8));
        var (resolver, _) = Make(keyFile: false);

        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, null, true).Kind);

        Assert.Empty(resolver.Failures.TakeBlocking(Map, 0, resolver.Failures.CurrentSequence));
        MapLoadFailure last = resolver.Failures.LastNavMeshCause(Map);
        Assert.NotNull(last);
        Assert.Equal(NavMeshCause.NoKey, last.NavMeshCause);
        Assert.Equal("arena_test.json", last.File);
    }

    [Fact]
    public void ANavMeshBundleWithoutAVerifiedArenaIsUnverified()
    {
        string path = _bed.WriteBundle("arena_test_navmesh.bundle", KeyBed.Bytes(5000, 8));
        var (resolver, env) = Make();
        env.Verified = false;

        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, null, true).Kind);

        Assert.Equal(NavMeshCause.Unverified, resolver.Failures.LastNavMeshCause(Map).NavMeshCause);
    }

    [Fact]
    public void AMissingOrDamagedNavMeshBundleHasItsOwnCauses()
    {
        var (resolver, _) = Make();
        Assert.Equal(PreparedKind.Refused, resolver.Prepare(Path.Combine(_bed.Bundles, "missing_navmesh.bundle"), Map, null, true).Kind);
        Assert.Equal(NavMeshCause.FileMissing, resolver.Failures.LastNavMeshCause(Map).NavMeshCause);

        string damaged = _bed.WriteBundle("arena_test_navmesh.bundle", KeyBed.Bytes(5000, 8), kMap: KeyBed.OtherKMap);
        Assert.Equal(PreparedKind.Refused, resolver.Prepare(damaged, Map, null, true).Kind);
        Assert.Equal(NavMeshCause.Damaged, resolver.Failures.LastNavMeshCause(Map).NavMeshCause);

        Assert.Empty(resolver.Failures.TakeBlocking(Map, 0, resolver.Failures.CurrentSequence));
    }

    // ---- integrity ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADamagedChunkIsRecordedOnceAsIntegrityWithTheBundleName()
    {
        var (resolver, env) = Make();
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(3 * 4096, 3), 4096);
        KeyBed.FlipByte(path, Ampb.Inspect(path).Header.PayloadOffset + 4096 + 10); // inside chunk 1
        var prepared = resolver.Prepare(path, Map, Served, false);
        Assert.Throws<AmpbException>(() =>
        {
            prepared.Stream.Position = 4096;
            prepared.Stream.ReadExactly(new byte[100], 0, 100); // throws at the damaged chunk; ReadExactly makes the result of the read count (CA2022)
        });

        resolver.Streams.ReportError(prepared);
        resolver.Streams.ReportError(prepared); // reported once per stream

        Assert.Single(env.Errors);
        Assert.True(resolver.Failures.HasIntegrity);
        MapLoadFailure record = Assert.Single(resolver.Failures.TakeIntegrity());
        Assert.Equal(Map, record.MapKey);
        Assert.Equal("arena_test_scenes.bundle", record.File);
        Assert.Contains("chunk 1", record.Detail);
        prepared.Dispose();
    }

    [Fact]
    public void ADamagedNavMeshStreamIsANavMeshCauseNotAnIntegrityRecord()
    {
        var (resolver, _) = Make();
        string path = _bed.WriteBundle("arena_test_navmesh.bundle", KeyBed.Bytes(2 * 4096, 3), 4096);
        KeyBed.FlipByte(path, Ampb.Inspect(path).Header.PayloadOffset + 5);
        var prepared = resolver.Prepare(path, Map, null, true);
        Assert.Throws<AmpbException>(() => prepared.Stream.Read(new byte[10], 0, 10));

        resolver.Streams.ReportError(prepared);

        Assert.False(resolver.Failures.HasIntegrity);
        Assert.Equal(NavMeshCause.Damaged, resolver.Failures.LastNavMeshCause(Map).NavMeshCause);
        prepared.Dispose();
    }

    [Fact]
    public void ARegistryBuiltWithoutFailuresRecordsNothingAndStillReportsTheError()
    {
        var env = new FakeKeyEnvironment(_bed);
        var streams = new BundleStreamRegistry(env); // the older constructor: no record is kept, the Error line stays
        _bed.WriteKeyFile();
        var resolver = new BundleKeyResolver(env);
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(2 * 4096, 3), 4096);
        KeyBed.FlipByte(path, Ampb.Inspect(path).Header.PayloadOffset + 5);
        var prepared = resolver.Prepare(path, Map, Served, false);
        Assert.Throws<AmpbException>(() => prepared.Stream.Read(new byte[10], 0, 10));

        streams.ReportError(prepared);

        Assert.Single(env.Errors);
        Assert.False(resolver.Failures.HasIntegrity);
        prepared.Dispose();
    }

    // ---- the registry's own plumbing ---------------------------------------------------------------------------------------------

    [Fact]
    public void NoteSafeNeverThrowsWhateverTheNoteDoes()
    {
        var (resolver, _) = Make();
        resolver.NoteSafe(failures => throw new InvalidOperationException("boom"));
        resolver.NoteSafe(null);
        resolver.NoteSafe(failures => failures.NoteBundle(Map, MapLoadFailureKind.BundleMissing, "a.bundle", "missing"));
        Assert.Equal(1, resolver.Failures.CurrentSequence);
    }

    [Fact]
    public void TheNotedCallbackSeesEveryStoredRecordOnceAndItsFailureIsSwallowed()
    {
        var (resolver, _) = Make();
        var seen = new List<MapLoadFailure>();
        resolver.Failures.Noted = failure => seen.Add(failure);
        resolver.NoteSafe(failures => failures.NoteBundle(Map, MapLoadFailureKind.BundleMissing, "a.bundle", "missing"));
        resolver.NoteSafe(failures => failures.NoteNavMesh(Map, NavMeshCause.FileMissing, "gone", "n.bundle"));
        resolver.Failures.Note(null);                                       // nothing stored, no callback
        resolver.Failures.Note(new MapLoadFailure { MapKey = "" });         // no map: nothing stored

        Assert.Equal(new long[] { 1, 2 }, seen.Select(record => record.Sequence).ToArray());
        Assert.Equal(MapLoadFailureKind.NavMesh, seen[1].Kind);

        resolver.Failures.Noted = _ => throw new InvalidOperationException("a log line failed");
        Assert.Equal(3, resolver.Failures.NoteBundle(Map, MapLoadFailureKind.BundleMissing, "b.bundle", "missing"));
        Assert.Equal(3, resolver.Failures.CurrentSequence);
    }

    [Fact]
    public void TheRecordsHoldNoKeyMaterial()
    {
        var wrap = _bed.MakeWrap();
        _bed.WriteKeyFile(Map, wrap);
        KeyBed.FlipByte(_bed.SourcePath(0), 3);
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, 7));
        var (resolver, _) = Make(keyFile: false);
        Assert.Equal(PreparedKind.Refused, resolver.Prepare(path, Map, Served, false).Kind);

        MapLoadFailure record = Only(resolver);
        string text = string.Join("|", record.Detail, record.File, record.BuiltFor, record.InstalledVersion, record.FailedPath, record.KeyCause);
        foreach (string secret in new[] { KeyBed.Hex(KeyBed.KMap), KeyBed.Hex(wrap.Salt), KeyBed.Hex(wrap.WrappedKey), KeyBed.Hex(wrap.Check) })
            Assert.DoesNotContain(secret, text);
    }
}
