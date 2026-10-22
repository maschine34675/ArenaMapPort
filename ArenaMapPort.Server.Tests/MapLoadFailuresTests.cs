#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// MapLoadFailures: the thread-safe per-map record of what went wrong while a map loaded. Sequence numbers, blocking by attempt window, map and kind,
/// NavMesh records that never block and survive consumption, integrity records that outlive an attempt, the cap, and "never throws".
/// (What BundleKeyResolver.Prepare, BundleHost and NavMeshInstaller record is tested with their own wiring.)
/// </summary>
public sealed class MapLoadFailuresTests
{
    [Fact]
    public void EveryNoteGetsTheNextSequenceNumber()
    {
        var failures = new MapLoadFailures();
        Assert.Equal(0, failures.CurrentSequence);
        Assert.Equal(1, failures.NoteGateRefused("arena_bowl", "not verified"));
        Assert.Equal(2, failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, "a.bundle", "missing"));
        Assert.Equal(3, failures.NoteIntegrity("arena_bay5", "b.bundle", "chunk 3"));
        Assert.Equal(4, failures.NoteNavMesh("arena_bowl", NavMeshCause.NoKey, "no key"));
        Assert.Equal(5, failures.NoteKey("arena_bowl", KeyFailure.ContentDiffers, "d", "0.6.0", "0.6.1", "level0", "arena_bowl.json"));
        Assert.Equal(5, failures.CurrentSequence);
        Assert.Equal(5, failures.Count);
    }

    [Fact]
    public void TheRecordHoldsWhatTheCallerGave()
    {
        var failures = new MapLoadFailures();
        failures.NoteKey("arena_bowl", KeyFailure.SizeDiffers, "size differs", "0.6.0.0.47752", "0.6.1.0.48206", @"EscapeFromTarkovArena_Data\sharedassets5.assets.resS", "arena_bowl.json");
        MapLoadFailure record = failures.TakeBlocking("arena_bowl", 0, 1).Single();
        Assert.Equal(1, record.Sequence);
        Assert.Equal(MapLoadFailureKind.Key, record.Kind);
        Assert.Equal(KeyFailure.SizeDiffers, record.KeyCause);
        Assert.Equal("size differs", record.Detail);
        Assert.Equal("0.6.0.0.47752", record.BuiltFor);
        Assert.Equal("0.6.1.0.48206", record.InstalledVersion);
        Assert.Equal(@"EscapeFromTarkovArena_Data\sharedassets5.assets.resS", record.FailedPath);
        Assert.Equal("arena_bowl.json", record.File);
    }

    [Fact]
    public void ARecordIsStoredAsACopy()
    {
        var failures = new MapLoadFailures();
        var original = new MapLoadFailure { MapKey = "arena_bowl", Kind = MapLoadFailureKind.BundleMissing, File = "a" };
        failures.Note(original);
        original.File = "changed";
        Assert.Equal("a", failures.TakeBlocking("arena_bowl", 0, 1).Single().File);
        Assert.Equal(0, original.Sequence); // the caller's object is not the stored one
    }

    [Fact]
    public void TakeBlockingReturnsAndConsumesTheRecordsOfTheWindowAndTheMap()
    {
        var failures = new MapLoadFailures();
        failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, "before.bundle", "x"); // 1: before the attempt
        long start = failures.CurrentSequence;
        failures.NoteGateRefused("arena_bowl", "x"); // 2
        failures.NoteBundle("arena_bay5", MapLoadFailureKind.BundleMissing, "other.bundle", "x"); // 3: another map
        failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleInvalid, "b.bundle", "x"); // 4
        long end = failures.CurrentSequence;
        failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleUnreadable, "after.bundle", "x"); // 5: after the attempt

        IReadOnlyList<MapLoadFailure> taken = failures.TakeBlocking("arena_bowl", start, end);
        Assert.Equal(new long[] { 2, 4 }, taken.Select(record => record.Sequence));

        // consumed: a second take finds nothing of this window, and the others are still there
        Assert.Empty(failures.TakeBlocking("arena_bowl", start, end));
        Assert.Equal(3, failures.Count);
        Assert.Single(failures.TakeBlocking("arena_bay5", 0, 10));
        Assert.Equal(new long[] { 1, 5 }, failures.TakeBlocking("arena_bowl", 0, 10).Select(record => record.Sequence));
    }

    [Fact]
    public void TheWindowIsExclusiveAtTheStartAndInclusiveAtTheEnd()
    {
        var failures = new MapLoadFailures();
        failures.NoteGateRefused("arena_bowl", "1");
        failures.NoteGateRefused("arena_bowl", "2");
        failures.NoteGateRefused("arena_bowl", "3");
        Assert.Equal(new long[] { 2 }, failures.TakeBlocking("arena_bowl", 1, 2).Select(record => record.Sequence));
    }

    [Fact]
    public void NavMeshRecordsAreNeverBlockingAndTheLastOneSurvivesConsumption()
    {
        var failures = new MapLoadFailures();
        Assert.Null(failures.LastNavMeshCause("arena_bowl"));
        failures.NoteNavMesh("arena_bowl", NavMeshCause.FileMissing, "missing");
        failures.NoteNavMesh("arena_bowl", NavMeshCause.NoKey, "no key");
        failures.NoteNavMesh("arena_bay5", NavMeshCause.Damaged, "damaged");

        Assert.Empty(failures.TakeBlocking("arena_bowl", 0, 100));
        Assert.Equal(3, failures.Count); // nothing was consumed

        MapLoadFailure last = failures.LastNavMeshCause("arena_bowl");
        Assert.Equal(NavMeshCause.NoKey, last.NavMeshCause);
        Assert.Equal(2, last.Sequence);
        Assert.Equal(NavMeshCause.NoKey, failures.LastNavMeshCause("arena_bowl").NavMeshCause); // read twice: not consumed
        Assert.Equal(NavMeshCause.Damaged, failures.LastNavMeshCause("arena_bay5").NavMeshCause);
        Assert.Null(failures.LastNavMeshCause("arena_airpit"));
        Assert.Null(failures.LastNavMeshCause(null));
        Assert.Null(failures.LastNavMeshCause(""));
    }

    [Fact]
    public void LastNavMeshCauseSurvivesEvenWhenTheCapDropsTheOldRecords()
    {
        var failures = new MapLoadFailures();
        failures.NoteNavMesh("arena_bowl", NavMeshCause.FileMissing, "missing");
        for (int i = 0; i < MapLoadFailures.MaxRecords + 10; i++)
            failures.NoteBundle("arena_bay5", MapLoadFailureKind.BundleMissing, "x", "x");
        Assert.Equal(MapLoadFailures.MaxRecords, failures.Count);
        Assert.Equal(NavMeshCause.FileMissing, failures.LastNavMeshCause("arena_bowl").NavMeshCause);
    }

    [Fact]
    public void IntegrityRecordsOutsideAnyAttemptAreTakenSeparately()
    {
        var failures = new MapLoadFailures();
        Assert.False(failures.HasIntegrity);
        failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, "a", "x");
        failures.NoteIntegrity("arena_bay5", "scenes.bundle", "chunk 3");
        failures.NoteIntegrity("arena_equator", "preset.bundle", "chunk 0");
        Assert.True(failures.HasIntegrity);

        IReadOnlyList<MapLoadFailure> integrity = failures.TakeIntegrity();
        Assert.Equal(new[] { "arena_bay5", "arena_equator" }, integrity.Select(record => record.MapKey));
        Assert.False(failures.HasIntegrity);
        Assert.Empty(failures.TakeIntegrity());
        Assert.Equal(1, failures.Count); // the bundle record is not integrity and stays
    }

    [Fact]
    public void AnIntegrityRecordInsideAFailedAttemptIsClaimedByTheAttemptNotByTheLateNotice()
    {
        var failures = new MapLoadFailures();
        long start = failures.CurrentSequence;
        failures.NoteIntegrity("arena_bowl", "scenes.bundle", "chunk 3");
        long end = failures.CurrentSequence;

        Assert.Single(failures.TakeBlocking("arena_bowl", start, end));
        Assert.False(failures.HasIntegrity);
        Assert.Empty(failures.TakeIntegrity());
    }

    [Fact]
    public void ARepeatedCachedKeyFailureRecordsAgainPerAttempt()
    {
        // BundleKeyResolver notes the cached key failure in every refusal branch, so each attempt has its own record
        var failures = new MapLoadFailures();
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            long start = failures.CurrentSequence;
            failures.NoteKey("arena_bowl", KeyFailure.ContentDiffers, "d", "0.6.0", "0.6.1", null, "arena_bowl.json");
            failures.NoteKey("arena_bowl", KeyFailure.ContentDiffers, "d", "0.6.0", "0.6.1", null, "arena_bowl.json");
            Assert.Equal(2, failures.TakeBlocking("arena_bowl", start, failures.CurrentSequence).Count);
        }
        Assert.Equal(0, failures.Count);
    }

    [Fact]
    public void TheListIsCappedAndTheOldestRecordsGoFirst()
    {
        var failures = new MapLoadFailures();
        for (int i = 0; i < MapLoadFailures.MaxRecords + 5; i++)
            failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, "f" + i, "x");
        Assert.Equal(MapLoadFailures.MaxRecords, failures.Count);
        Assert.Equal(MapLoadFailures.MaxRecords + 5, failures.CurrentSequence);
        Assert.Equal(6, failures.TakeBlocking("arena_bowl", 0, long.MaxValue).First().Sequence);
    }

    [Fact]
    public void TheRegistryNeverThrows()
    {
        var failures = new MapLoadFailures();
        Assert.Equal(0, failures.Note(null));
        Assert.Equal(0, failures.Note(new MapLoadFailure { MapKey = null, Kind = MapLoadFailureKind.BundleMissing }));
        Assert.Equal(0, failures.Note(new MapLoadFailure { MapKey = "", Kind = MapLoadFailureKind.BundleMissing }));
        Assert.Equal(0, failures.NoteGateRefused(null, null));
        Assert.Empty(failures.TakeBlocking(null, 0, 10));
        Assert.Empty(failures.TakeBlocking("arena_bowl", 10, 0));
        Assert.Empty(failures.TakeIntegrity());
        Assert.False(failures.HasIntegrity);
        Assert.Equal(0, failures.CurrentSequence);

        // null text fields are fine
        Assert.Equal(1, failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleLoadFailed, null, null));
        Assert.Equal(2, failures.NoteKey("arena_bowl", KeyFailure.Internal, null, null, null, null, null));
    }

    [Fact]
    public async Task ConcurrentWritersAndReadersKeepTheSequenceUniqueAndLoseNothing()
    {
        var failures = new MapLoadFailures();
        const int threads = 8;
        const int perThread = 25; // 200 records in all, below the cap
        var sequences = new System.Collections.Concurrent.ConcurrentBag<long>();
        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
            {
                sequences.Add(failures.NoteBundle("arena_bowl", MapLoadFailureKind.BundleMissing, "f", "x"));
                failures.CurrentSequence.ToString();
                failures.HasIntegrity.ToString();
                failures.LastNavMeshCause("arena_bowl");
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(threads * perThread, sequences.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, threads * perThread).Select(i => (long)i), sequences.OrderBy(s => s));
        Assert.Equal(threads * perThread, failures.TakeBlocking("arena_bowl", 0, long.MaxValue).Count);
    }

    [Fact]
    public void ResetForgetsEverythingIncludingTheSequence()
    {
        var failures = new MapLoadFailures();
        failures.NoteNavMesh("arena_bowl", NavMeshCause.NoKey, "x");
        failures.NoteIntegrity("arena_bowl", "f", "x");
        failures.ResetForTests();
        Assert.Equal(0, failures.Count);
        Assert.Equal(0, failures.CurrentSequence);
        Assert.Null(failures.LastNavMeshCause("arena_bowl"));
        Assert.False(failures.HasIntegrity);
    }
}
