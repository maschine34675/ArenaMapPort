#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Crypto;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The Unity-free chain of stage 2 and the glue's inputs run together: the real <see cref="BundleKeyResolver"/> refuses a bundle (a key that does not
/// open, a missing key file, a damaged container, an Arena that is not verified, a damaged chunk), its failure registry feeds the real
/// <see cref="MapNoticeScheduler"/> over a scripted environment, and the modal or toast the player would read is checked. This is what
/// <c>BundleHost</c>, <c>LoadingScreenArt</c> and <c>NavMeshInstaller</c> wire up inside EFT, minus EFT.
/// </summary>
public sealed class MapNoticeEndToEndTests : IDisposable
{
    const string Map = KeyBed.Map;
    const string Served = "arena_test/arena_test_scenes.bundle";

    readonly KeyBed _bed = new();
    readonly FakeKeyEnvironment _keyEnv;
    readonly BundleKeyResolver _resolver;
    readonly FakeNoticeEnvironment _env;
    readonly MapNoticeScheduler _sched;

    public MapNoticeEndToEndTests()
    {
        _keyEnv = new FakeKeyEnvironment(_bed);
        _resolver = new BundleKeyResolver(_keyEnv);
        _env = new FakeNoticeEnvironment
        {
            Maps = new[] { new MapInfo(Map, "Test Map", "arena_test_preset.bundle", "arena_test_scenes.bundle", "arena_test_navmesh.bundle", "material-shaders.arena_test.json") },
            Locations = NoticeTestData.Offered(Map),
        };
        _sched = new MapNoticeScheduler(_env, _resolver.Failures);
    }

    public void Dispose() => _bed.Dispose();

    void Run(double seconds)
    {
        for (int i = 0; i < (int)Math.Round(seconds / 0.25); i++)
        {
            _env.Now += 0.25;
            _sched.Tick();
        }
    }

    /// <summary>The first main menu with a healthy client: T1 has nothing to say, the scheduler goes idle.</summary>
    void HealthyFirstMenu()
    {
        _sched.OnMenuShown(true);
        Run(3);
        Assert.True(_sched.T1Done);
        Assert.Empty(_env.Modals);
    }

    /// <summary>A raid attempt for the test map whose loading step is <paramref name="load"/>, then back to the main menu.</summary>
    void FailedAttempt(Action load)
    {
        _sched.NoteAttemptStarted(Map);
        load();
        _env.MenuIsCurrent = false;
        Run(5);
        _sched.NoteComeback();
        Run(1);
        _env.MenuIsCurrent = true;
        _sched.OnMenuShown(true);
        Run(3);
    }

    string WriteBundle(int seed = 7, byte[] kMap = null) =>
        _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(5000, seed), kMap: kMap);

    string OnlyModalText() => Assert.Single(_env.Modals).Text;

    // ---- key failures (stage 2) -------------------------------------------------------------------------------------------

    [Fact]
    public void AnArenaUpdateThatChangedTheFilesIsSaidToNeedANewerArenaMapPort()
    {
        _bed.WriteKeyFile(Map, _bed.MakeWrap(builtFor: KeyBed.OlderVersion));
        File.AppendAllText(_bed.SourcePath(0), "grown"); // the installed Arena (0.6.1) has other files than the ones the key was built from (0.6.0)
        string path = WriteBundle();
        HealthyFirstMenu();

        FailedAttempt(() => _resolver.Prepare(path, Map, Served, false));

        string text = OnlyModalText();
        Assert.StartsWith("Test Map cannot be opened: Arena " + KeyBed.Version + " is installed, but this ArenaMapPort was built for Arena " + KeyBed.OlderVersion, text);
        Assert.Contains("Install the newest ArenaMapPort release", text);
        Assert.EndsWith("ref KEY_UPDATED", text);
    }

    [Fact]
    public void AnArenaOlderThanTheKeysIsToldToUpdateArena()
    {
        _bed.WriteKeyFile(Map, _bed.MakeWrap(builtFor: "0.6.2.0.50000"));
        File.AppendAllText(_bed.SourcePath(0), "grown");
        string path = WriteBundle();
        HealthyFirstMenu();

        FailedAttempt(() => _resolver.Prepare(path, Map, Served, false));

        string text = OnlyModalText();
        Assert.StartsWith("Test Map cannot be opened: this ArenaMapPort was built for Arena 0.6.2.0.50000, but Arena " + KeyBed.Version + " is installed.", text);
        Assert.Contains("Update Arena in the Battlestate Games launcher", text);
        Assert.EndsWith("ref KEY_ARENA_OLDER", text);
    }

    [Fact]
    public void FilesOfTheSameBuildThatDoNotUnlockTheKeyAreToldToBeRepaired()
    {
        _bed.WriteKeyFile();
        KeyBed.FlipByte(_bed.SourcePath(0), 3); // same sizes, same version, other content
        string path = WriteBundle();
        HealthyFirstMenu();

        FailedAttempt(() => _resolver.Prepare(path, Map, Served, false));

        string text = OnlyModalText();
        Assert.StartsWith("Test Map cannot be opened: the Arena files it is built from do not match ArenaMapPort's unlock file (Arena " + KeyBed.Version + ").", text);
        Assert.Contains("Repair Arena in the Battlestate Games launcher", text);
        Assert.EndsWith("ref KEY_DIFFERS", text);
    }

    [Fact]
    public void AMissingKeyFileNamesTheFile()
    {
        string path = WriteBundle();
        HealthyFirstMenu();

        FailedAttempt(() => _resolver.Prepare(path, Map, Served, false));

        string text = OnlyModalText();
        Assert.StartsWith("Test Map cannot be opened: ArenaMapPort's unlock file for it (arena_test.json) is missing or damaged.", text);
        Assert.EndsWith("ref KEY_FILE_BAD", text);
    }

    [Fact]
    public void AnArenaFileThatCannotBeReadIsToldToCloseWhateverHoldsIt()
    {
        _bed.WriteKeyFile();
        File.Delete(_bed.SourcePath(1));
        string path = WriteBundle();
        HealthyFirstMenu();

        FailedAttempt(() => _resolver.Prepare(path, Map, Served, false));

        string text = OnlyModalText();
        Assert.StartsWith("Test Map cannot be opened: ArenaMapPort cannot read an Arena file it needs", text);
        Assert.EndsWith("ref KEY_UNREADABLE", text);
    }

    [Fact]
    public void ARepeatedFailureAnswersEveryAttemptAgainWithoutDerivingTheKeyAgain()
    {
        string path = WriteBundle();
        HealthyFirstMenu();

        FailedAttempt(() => _resolver.Prepare(path, Map, Served, false));
        Assert.Single(_env.Modals);
        _env.LastTicket.Accept(_env.Now);
        Run(1);
        FailedAttempt(() => _resolver.Prepare(path, Map, Served, false));

        Assert.Equal(2, _env.Modals.Count);
        Assert.All(_env.Modals, modal => Assert.EndsWith("ref KEY_FILE_BAD", modal.Text));
        Assert.Equal(1, _resolver.Derivations);
    }

    // ---- bundle problems -------------------------------------------------------------------------------------------------------

    [Fact]
    public void ABundleThatIsNotAValidContainerNamesItsFile()
    {
        _bed.WriteKeyFile();
        string path = WriteBundle(kMap: KeyBed.OtherKMap); // another key set: the header does not authenticate
        HealthyFirstMenu();

        FailedAttempt(() => _resolver.Prepare(path, Map, Served, false));

        string text = OnlyModalText();
        Assert.StartsWith("Test Map could not be loaded: one of ArenaMapPort's map files is missing or damaged (arena_test_scenes.bundle).", text);
        Assert.EndsWith("ref BUNDLE_BAD", text);
    }

    [Fact]
    public void AMissingBundleIsTheSameMessage()
    {
        _bed.WriteKeyFile();
        HealthyFirstMenu();

        FailedAttempt(() => _resolver.Prepare(Path.Combine(_bed.Bundles, "arena_test_preset.bundle"), Map, "arena_test/arena_test_preset.bundle", false));

        string text = OnlyModalText();
        Assert.Contains("(arena_test_preset.bundle)", text);
        Assert.EndsWith("ref BUNDLE_BAD", text);
    }

    [Fact]
    public void AnArenaThatIsNotVerifiedAnswersWithTheClientGateText()
    {
        string path = WriteBundle();
        _keyEnv.Verified = false;
        HealthyFirstMenu();
        _env.Gate = NoticeTestData.NotVerified(ArenaMapPort.Shared.ArenaInstallCause.NotRegistered, "none"); // what the client's check says by now

        FailedAttempt(() => _resolver.Prepare(path, Map, Served, false));

        string text = OnlyModalText();
        Assert.StartsWith("Test Map could not be loaded: ArenaMapPort did not find Escape from Tarkov: Arena on this PC.", text);
        Assert.DoesNotContain("To hide this message", text); // ShowMapNotices never silences the answer to a failed raid
        Assert.EndsWith("ref NO_ARENA", text);
    }

    [Fact]
    public void AMapThatLoadedAndEndedWithoutAFailureIsNotAnswered()
    {
        _bed.WriteKeyFile();
        string path = WriteBundle();
        HealthyFirstMenu();

        _sched.NoteAttemptStarted(Map);
        using (_resolver.Prepare(path, Map, Served, false)) { }
        _env.InStartedRaid = true;
        _env.CurrentRaidMapKey = Map;
        Run(1);
        _env.InStartedRaid = false;
        _env.CurrentRaidMapKey = null;
        _sched.NoteComeback();
        Run(2);
        _sched.OnMenuShown(true);
        Run(3);

        Assert.Empty(_env.Modals);
        Assert.Empty(_env.Toasts);
    }

    // ---- damage found in a raid that ran, and the NavMesh -----------------------------------------------------------------------

    [Fact]
    public void ADamagedChunkInARaidThatRanIsAToastAtTheNextMenu()
    {
        _bed.WriteKeyFile();
        string path = _bed.WriteBundle("arena_test_scenes.bundle", KeyBed.Bytes(3 * 4096, 3), 4096);
        KeyBed.FlipByte(path, Ampb.Inspect(path).Header.PayloadOffset + 4096 + 10);
        HealthyFirstMenu();

        _sched.NoteAttemptStarted(Map);
        var prepared = _resolver.Prepare(path, Map, Served, false);
        _env.InStartedRaid = true;
        _env.CurrentRaidMapKey = Map;
        Run(1); // the raid started: the attempt succeeded
        Assert.Throws<AmpbException>(() =>
        {
            prepared.Stream.Position = 4096;
            prepared.Stream.ReadExactly(new byte[100], 0, 100); // throws at the damaged chunk; ReadExactly makes the result of the read count (CA2022)
        });
        _resolver.Streams.ReportError(prepared); // found while the raid ran
        _env.InStartedRaid = false;
        _env.CurrentRaidMapKey = null;
        _sched.NoteComeback();
        _sched.OnMenuShown(true);
        Run(3);

        Assert.Empty(_env.Modals);
        string toast = Assert.Single(_env.Toasts);
        Assert.StartsWith("ArenaMapPort: a map file for Test Map reported damage during the raid (arena_test_scenes.bundle).", toast);
        prepared.Dispose();
    }

    [Fact]
    public void ARaidWithoutNavMeshShowsTheToastOnceItHasStartedWithTheReasonFromTheLoader()
    {
        string navmesh = _bed.WriteBundle("arena_test_navmesh.bundle", KeyBed.Bytes(5000, 8));
        HealthyFirstMenu();

        _sched.NoteAttemptStarted(Map);
        Assert.Equal(PreparedKind.Refused, _resolver.Prepare(navmesh, Map, null, true).Kind); // no key file: the NavMesh bundle cannot be opened
        _sched.NoteRaidWithoutNavMesh(Map);
        Run(1);
        Assert.Empty(_env.Toasts); // not before the raid has started
        _env.InStartedRaid = true;
        _env.CurrentRaidMapKey = Map;
        Run(1);

        string toast = Assert.Single(_env.Toasts);
        Assert.Equal(
            "ArenaMapPort: Test Map: the navigation data could not be loaded (its unlock file could not be used); bots will not move. Reinstall the complete client folder. " +
            "Details: BepInEx\\LogOutput.log, lines [NavMesh].", toast);
        Assert.Empty(_env.Modals);
    }

    [Fact]
    public void ANavMeshProblemIsNeverTheAnswerToAFailedAttempt()
    {
        _bed.WriteKeyFile();
        string navmesh = _bed.WriteBundle("arena_test_navmesh.bundle", KeyBed.Bytes(5000, 8), kMap: KeyBed.OtherKMap);
        HealthyFirstMenu();

        // the scenes loaded, the NavMesh bundle did not, and the attempt still failed for no recorded reason: not the NavMesh's fault
        FailedAttempt(() => _resolver.Prepare(navmesh, Map, null, true));

        string text = OnlyModalText();
        Assert.EndsWith("ref NO_CAUSE", text);
    }
}
