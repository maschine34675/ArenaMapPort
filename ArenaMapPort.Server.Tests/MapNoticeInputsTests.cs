#nullable disable
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// MapNoticeInputs: the pure builder behind the main-menu notice. The offered check (enabled locations only, unknown list), the cause-to-row mapping of the
/// client's own Arena check, NeedsProbe, the client file check through its seam (bundles, shader sidecar, key file only for AMPB bundles), the registry
/// view of the maps and the log fragments.
/// </summary>
public sealed class MapNoticeInputsTests
{
    const string PluginDir = @"C:\Game\BepInEx\plugins\maschine-ArenaMapPort";

    // ---- the registry view ---------------------------------------------------------------------------------------

    [Fact]
    public void TheRegistryMapsCarryTheirNamesAndFileNames()
    {
        IReadOnlyList<MapInfo> maps = MapInfo.FromRegistry();
        Assert.Equal(ArenaMapRegistry.All.Select(map => map.Key), maps.Select(map => map.Key));
        Assert.Equal(new[] { "Equator", "Bay 5", "Air Pit", "Bowl" }, maps.Select(map => map.Name));

        MapInfo bay5 = maps.Single(map => map.Key == "arena_bay5");
        ArenaMapDescriptor descriptor = ArenaMapRegistry.All.Single(map => map.Key == "arena_bay5");
        Assert.Equal(descriptor.PresetBundleFileName, bay5.PresetFile);
        Assert.Equal(descriptor.ScenesBundleFileName, bay5.ScenesFile);
        Assert.Equal(descriptor.NavMeshBundleFileName, bay5.NavMeshFile);
        Assert.Equal("material-shaders.arena_bay5.json", bay5.SidecarFile);
    }

    [Theory]
    [InlineData("Arena: Equator", "Equator")]
    [InlineData("Arena: Bay 5", "Bay 5")]
    [InlineData("Arena:Bowl", "Bowl")]
    [InlineData("Plain", "Plain")]
    [InlineData(null, "")]
    public void TheDisplayNameDropsTheArenaPrefix(string locale, string expected)
    {
        Assert.Equal(expected, MapInfo.DisplayName(locale));
    }

    [Fact]
    public void TheSidecarNameIsTheOneTheShaderRebindAndThePluginUse()
    {
        string root = RepoRoot();
        string rebind = File.ReadAllText(Path.Combine(root, "ArenaMapPort.Client", "ShaderRebind.cs"));
        Assert.Contains("SidecarFileName(ArenaMapDescriptor map) => $\"material-shaders.{map.Key}.json\";", rebind);

        // the file check covers the files Plugin.LogExpectedFile checks at startup: the three bundles and the sidecar of every map
        string plugin = File.ReadAllText(Path.Combine(root, "ArenaMapPort.Client", "Plugin.cs"));
        Assert.Contains("LogExpectedFile(Path.Combine(bundlesDir, map.PresetBundleFileName));", plugin);
        Assert.Contains("LogExpectedFile(Path.Combine(bundlesDir, map.ScenesBundleFileName));", plugin);
        Assert.Contains("LogExpectedFile(Path.Combine(bundlesDir, map.NavMeshBundleFileName));", plugin);
        Assert.Contains("LogExpectedFile(Path.Combine(pluginDir, ShaderRebind.SidecarFileName(map)));", plugin);
    }

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "ArenaMapPort.Client", "Plugin.cs")))
                return dir.FullName;
        throw new FileNotFoundException("repository root");
    }

    // ---- the offered check -----------------------------------------------------------------------------------------

    [Fact]
    public void OnlyEnabledLocationsWithARegistryKeyAreOffered()
    {
        var locations = new List<OfferedLocation>
        {
            new("arena_equator", true), new("arena_bay5", false), new("arena_bowl", true), new("factory4_day", true),
            new("arena_unknown", true), new(null, true), new("", true), null,
        };
        var offered = MapNoticeInputs.OfferedKeys(NoticeTestData.Maps, locations);
        Assert.Equal(new[] { "arena_equator", "arena_bowl" }, offered); // registry order, enabled only, no foreign key
    }

    [Fact]
    public void AnUnknownLocationListIsNotAnEmptyOne()
    {
        Assert.Null(MapNoticeInputs.OfferedKeys(NoticeTestData.Maps, null));
        Assert.Empty(MapNoticeInputs.OfferedKeys(NoticeTestData.Maps, new List<OfferedLocation>()));
        Assert.Empty(MapNoticeInputs.OfferedKeys(null, NoticeTestData.AllOffered()));
    }

    [Fact]
    public void TheIdMatchIsOrdinal()
    {
        Assert.Empty(MapNoticeInputs.OfferedKeys(NoticeTestData.Maps, new[] { new OfferedLocation("ARENA_EQUATOR", true) }));
    }

    [Fact]
    public void TheInputKnowsWhichRegistryMapsAreMissingAndWhatTheFileCheckCovers()
    {
        var input = NoticeTestData.Input(NoticeTestData.Verified(), NoticeTestData.Offered("arena_equator", "arena_bowl"));
        Assert.True(input.OfferedKnown);
        Assert.Equal(new[] { "arena_bay5", "arena_airpit" }, input.MissingMaps.Select(map => map.Key));
        Assert.Equal(new[] { "arena_equator", "arena_bowl" }, input.ScopeMaps.Select(map => map.Key));

        var unknown = NoticeTestData.Input(NoticeTestData.Verified(), null);
        Assert.False(unknown.OfferedKnown);
        Assert.Empty(unknown.MissingMaps);
        Assert.Equal(NoticeTestData.RegistryKeys, unknown.ScopeMaps.Select(map => map.Key)); // unknown: every registry map is checked

        var all = NoticeTestData.Input(NoticeTestData.Verified(), NoticeTestData.AllOffered());
        Assert.Empty(all.MissingMaps);
    }

    [Fact]
    public void BuildFillsInDefaultsForMissingParts()
    {
        MapAvailabilityInput input = MapNoticeInputs.Build(null, null, null, null, null, null, "0.3.0", true);
        Assert.Equal(GateState.NotStarted, input.Gate.State);
        Assert.Equal(4, input.Maps.Count);
        Assert.Equal(ProbeState.NotAsked, input.Probe.State);
        Assert.Empty(input.SkippedHooks);
        Assert.Null(input.Files);
    }

    // ---- NeedsProbe ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheServerIsAskedOnlyWhenThisClientIsVerifiedAndARegistryMapIsNotOffered()
    {
        Assert.True(MapNoticeInputs.NeedsProbe(NoticeTestData.Input(NoticeTestData.Verified(), NoticeTestData.Offered("arena_equator"))));
        Assert.True(MapNoticeInputs.NeedsProbe(NoticeTestData.Input(NoticeTestData.Verified(), new List<OfferedLocation>())));
        Assert.False(MapNoticeInputs.NeedsProbe(NoticeTestData.Healthy()));
        Assert.False(MapNoticeInputs.NeedsProbe(NoticeTestData.Input(NoticeTestData.Verified(), null)), "unknown offered list: no request");
        Assert.False(MapNoticeInputs.NeedsProbe(NoticeTestData.Input(NoticeTestData.NotVerified(ArenaInstallCause.NotRegistered), new List<OfferedLocation>())));
        Assert.False(MapNoticeInputs.NeedsProbe(NoticeTestData.Input(NoticeTestData.State(GateState.Running), new List<OfferedLocation>())));
        Assert.False(MapNoticeInputs.NeedsProbe(null));
    }

    // ---- the cause-to-row mapping --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(GateState.NotStarted, ArenaInstallCause.None, "registry", NoticeId.ClientCheckFailed, NoticeWhy.None)]
    [InlineData(GateState.CheckFailed, ArenaInstallCause.None, "registry", NoticeId.ClientCheckFailed, NoticeWhy.None)]
    [InlineData(GateState.TimedOut, ArenaInstallCause.None, "registry", NoticeId.ClientCheckSlow, NoticeWhy.None)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.NotRegistered, "none", NoticeId.ClientNoArena, NoticeWhy.None)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.RegistryUnavailable, "none", NoticeId.ClientRegistryUnreadable, NoticeWhy.None)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.FolderMissing, "override", NoticeId.ClientOverrideBad, NoticeWhy.FolderMissing)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.NotArenaFolder, "override", NoticeId.ClientOverrideBad, NoticeWhy.ExeMissing)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.ManifestMissing, "override", NoticeId.ClientOverrideBad, NoticeWhy.ManifestBad)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.ManifestInvalid, "override", NoticeId.ClientOverrideBad, NoticeWhy.ManifestBad)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.FolderMissing, "registry", NoticeId.ClientFolderBad, NoticeWhy.FolderMissing)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.NotArenaFolder, "registry", NoticeId.ClientFolderBad, NoticeWhy.ExeMissing)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.ManifestMissing, "registry", NoticeId.ClientFolderBad, NoticeWhy.ManifestBad)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.ManifestInvalid, "registry", NoticeId.ClientFolderBad, NoticeWhy.ManifestBad)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.FilesMismatch, "override", NoticeId.ClientIncomplete, NoticeWhy.None)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.FilesMismatch, "registry", NoticeId.ClientIncomplete, NoticeWhy.None)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.CheckFailed, "registry", NoticeId.ClientCheckFailed, NoticeWhy.None)]
    [InlineData(GateState.NotVerified, ArenaInstallCause.None, "registry", NoticeId.ClientCheckFailed, NoticeWhy.None)]
    public void EveryFailedStateOfTheClientsCheckMapsToItsRow(GateState state, ArenaInstallCause cause, string source, NoticeId expected, NoticeWhy why)
    {
        var gate = new GateSnapshot(state, cause, source, @"D:\Arena", null, 0, null, null);
        Assert.True(MapNoticeInputs.ClassifyClient(gate, out NoticeId id, out NoticeWhy actualWhy));
        Assert.Equal(expected, id);
        Assert.Equal(why, actualWhy);
    }

    [Fact]
    public void AVerifiedOrStillRunningCheckHasNoClientNotice()
    {
        Assert.False(MapNoticeInputs.ClassifyClient(NoticeTestData.Verified(), out _, out _));
        Assert.False(MapNoticeInputs.ClassifyClient(NoticeTestData.State(GateState.Running), out _, out _));
        Assert.True(MapNoticeInputs.ClassifyClient(null, out NoticeId id, out _));
        Assert.Equal(NoticeId.ClientCheckFailed, id);
    }

    [Fact]
    public void ASnapshotIsNormalisedAgainstItsResult()
    {
        var verified = new ArenaInstallationResult(true, @"D:\Arena", "registry", "0.6.1.0.48206", "0.6.0.0.47752", 10, 100, null, 0, null);
        GateSnapshot fromVerified = GateSnapshot.From(GateState.Running, verified, null);
        Assert.Equal(GateState.Verified, fromVerified.State); // a result wins over a stale state
        Assert.Equal(@"D:\Arena", fromVerified.InstallPath);
        Assert.Equal("0.6.1.0.48206", fromVerified.ManifestVersion);
        Assert.Equal(ArenaInstallCause.None, fromVerified.Cause);

        var failed = new ArenaInstallationResult(false, @"D:\Arena", "override", "0.6.1.0.48206", null, 0, 0, "reason text", 12, null, 0, ArenaInstallCause.FilesMismatch);
        GateSnapshot fromFailed = GateSnapshot.From(GateState.Verified, failed, null);
        Assert.Equal(GateState.NotVerified, fromFailed.State);
        Assert.Equal(ArenaInstallCause.FilesMismatch, fromFailed.Cause);
        Assert.Equal(12, fromFailed.MismatchCount);
        Assert.Equal("reason text", fromFailed.Reason);
        Assert.True(fromFailed.OverrideSource);

        var noCause = new ArenaInstallationResult(false, null, "none", null, null, 0, 0, "x", 0, null);
        Assert.Equal(ArenaInstallCause.CheckFailed, GateSnapshot.From(GateState.NotVerified, noCause, null).Cause); // a failure always names a cause

        GateSnapshot noResult = GateSnapshot.From(GateState.Verified, null, "boom");
        Assert.Equal(GateState.CheckFailed, noResult.State); // a finished check without a result is a failed one
        Assert.Equal("boom", noResult.FailureDetail);
        Assert.Equal(GateState.TimedOut, GateSnapshot.From(GateState.TimedOut, null, null).State);
    }

    [Theory]
    [InlineData(GateState.Verified, ArenaInstallCause.None, "verified:none", "verified")]
    [InlineData(GateState.NotVerified, ArenaInstallCause.NotRegistered, "not_verified:not_registered", "not_verified:not_registered")]
    [InlineData(GateState.NotVerified, ArenaInstallCause.FilesMismatch, "not_verified:files_mismatch", "not_verified:files_mismatch")]
    [InlineData(GateState.Running, ArenaInstallCause.None, "checking:none", "pending")]
    [InlineData(GateState.TimedOut, ArenaInstallCause.None, "timed_out:none", "pending")]
    [InlineData(GateState.CheckFailed, ArenaInstallCause.None, "check_failed:check_failed", "failed")]
    [InlineData(GateState.NotStarted, ArenaInstallCause.None, "not_started:check_failed", "failed")]
    public void TheGateHasItsLogCodes(GateState state, ArenaInstallCause cause, string logCode, string availability)
    {
        var gate = new GateSnapshot(state, cause, "registry", null, null, 0, null, null);
        Assert.Equal(logCode, gate.LogCode);
        Assert.Equal(availability, gate.AvailabilityCode);
    }

    // ---- the file check ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ACompleteInstallationHasNoMissingFile()
    {
        foreach (bool encrypted in new[] { true, false })
        {
            var files = FakeFileProbe.Complete(PluginDir, NoticeTestData.Maps, encrypted);
            FileScan scan = MapNoticeInputs.ScanFiles(PluginDir, NoticeTestData.Maps, files);
            Assert.Equal(0, scan.MissingCount);
            Assert.Null(scan.FirstMissing);
            Assert.Equal(4, scan.MapsChecked);
            Assert.Empty(scan.MapNames);
        }
    }

    [Fact]
    public void AMissingBundleOrSidecarIsReportedInTheFixedOrder()
    {
        var files = FakeFileProbe.Complete(PluginDir, NoticeTestData.Maps, false);
        MapInfo equator = NoticeTestData.Maps[0];
        MapInfo bay5 = NoticeTestData.Maps[1];
        files.Files.Remove(Path.Combine(PluginDir, "bundles", bay5.ScenesFile));
        files.Files.Remove(Path.Combine(PluginDir, "bundles", equator.NavMeshFile));
        files.Files.Remove(Path.Combine(PluginDir, equator.SidecarFile));
        files.Files.Remove(Path.Combine(PluginDir, "bundles", equator.PresetFile));

        FileScan scan = MapNoticeInputs.ScanFiles(PluginDir, NoticeTestData.Maps, files);
        Assert.Equal(new[] { equator.PresetFile, equator.NavMeshFile, equator.SidecarFile, bay5.ScenesFile }, scan.MissingFiles);
        Assert.Equal(equator.PresetFile, scan.FirstMissing);
        Assert.Equal(new[] { "Equator", "Bay 5" }, scan.MapNames);
        Assert.Equal(4, scan.MissingCount);
    }

    [Fact]
    public void TheKeyFileIsRequiredOnlyWhenThePresetBundleIsAnAmpbContainer()
    {
        MapInfo bowl = NoticeTestData.Maps[3];
        string keyFile = Path.Combine(PluginDir, "keys", "arena_bowl.json");

        // encrypted bundles: the key file must exist
        var encrypted = FakeFileProbe.Complete(PluginDir, new[] { bowl }, true);
        encrypted.Files.Remove(keyFile);
        FileScan missingKey = MapNoticeInputs.ScanFiles(PluginDir, new[] { bowl }, encrypted);
        Assert.Equal(new[] { "arena_bowl.json" }, missingKey.MissingFiles);
        Assert.Equal(new[] { "Bowl" }, missingKey.MapNames);

        // a development build (plaintext bundles): no key file, no notice
        var plain = FakeFileProbe.Complete(PluginDir, new[] { bowl }, false);
        Assert.DoesNotContain(keyFile, plain.Files);
        Assert.Equal(0, MapNoticeInputs.ScanFiles(PluginDir, new[] { bowl }, plain).MissingCount);
    }

    [Fact]
    public void AMissingPresetBundleDoesNotAlsoDemandAKeyFile()
    {
        MapInfo bowl = NoticeTestData.Maps[3];
        var files = FakeFileProbe.Complete(PluginDir, new[] { bowl }, true);
        files.Files.Remove(Path.Combine(PluginDir, "bundles", bowl.PresetFile));
        files.Files.Remove(Path.Combine(PluginDir, "keys", "arena_bowl.json"));
        FileScan scan = MapNoticeInputs.ScanFiles(PluginDir, new[] { bowl }, files);
        Assert.Equal(new[] { bowl.PresetFile }, scan.MissingFiles);
    }

    [Fact]
    public void OnlyTheMapsInScopeAreChecked()
    {
        var files = new FakeFileProbe(); // nothing exists
        FileScan scan = MapNoticeInputs.ScanFiles(PluginDir, new[] { NoticeTestData.Maps[3] }, files);
        Assert.Equal(1, scan.MapsChecked);
        Assert.Equal(4, scan.MissingCount);
        Assert.Equal(new[] { "Bowl" }, scan.MapNames);
        Assert.All(files.Asked, path => Assert.Contains("bowl", path));

        Assert.Equal(0, MapNoticeInputs.ScanFiles(PluginDir, new MapInfo[0], files).MissingCount);
        Assert.Equal(0, MapNoticeInputs.ScanFiles(PluginDir, null, files).MissingCount);
    }

    [Fact]
    public void TheScanPathsLieBelowTheBundlesFolderTheKeysFolderAndThePluginDirectory()
    {
        MapInfo equator = NoticeTestData.Maps[0];
        var files = FakeFileProbe.Complete(PluginDir, new[] { equator }, true);
        MapNoticeInputs.ScanFiles(PluginDir, new[] { equator }, files);
        Assert.Contains(Path.Combine(PluginDir, "bundles", equator.PresetFile), files.Asked);
        Assert.Contains(Path.Combine(PluginDir, "bundles", equator.ScenesFile), files.Asked);
        Assert.Contains(Path.Combine(PluginDir, "bundles", equator.NavMeshFile), files.Asked);
        Assert.Contains(Path.Combine(PluginDir, "material-shaders.arena_equator.json"), files.Asked);
        Assert.Contains(Path.Combine(PluginDir, "keys", "arena_equator.json"), files.Asked);
    }

    [Fact]
    public void TheDiskProbeSeesRealFilesAndTheAmpbMagicAndNeverThrows()
    {
        string dir = Path.Combine(Path.GetTempPath(), "map-notice-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "bundles"));
        Directory.CreateDirectory(Path.Combine(dir, "keys"));
        try
        {
            MapInfo bowl = NoticeTestData.Maps[3];
            File.WriteAllBytes(Path.Combine(dir, "bundles", bowl.PresetFile), new byte[] { 0x41, 0x4D, 0x50, 0x42, 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(dir, "bundles", bowl.ScenesFile), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(dir, "bundles", bowl.NavMeshFile), new byte[] { 1 });
            File.WriteAllText(Path.Combine(dir, bowl.SidecarFile), "{}");

            var probe = new DiskFileProbe();
            Assert.True(probe.Exists(Path.Combine(dir, "bundles", bowl.PresetFile)));
            Assert.True(probe.IsAmpb(Path.Combine(dir, "bundles", bowl.PresetFile)));
            Assert.False(probe.IsAmpb(Path.Combine(dir, "bundles", bowl.ScenesFile)));
            Assert.False(probe.Exists(Path.Combine(dir, "nope")));
            Assert.False(probe.IsAmpb(Path.Combine(dir, "nope")));
            Assert.False(probe.Exists("\0bad"));

            FileScan scan = MapNoticeInputs.ScanFiles(dir, new[] { bowl }, probe);
            Assert.Equal(new[] { "arena_bowl.json" }, scan.MissingFiles); // AMPB preset, no key file in this folder

            File.WriteAllText(Path.Combine(dir, "keys", "arena_bowl.json"), "{}");
            Assert.Equal(0, MapNoticeInputs.ScanFiles(dir, new[] { bowl }, probe).MissingCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AScanThatThrowsReportsWhatItFoundSoFar()
    {
        FileScan scan = MapNoticeInputs.ScanFiles(PluginDir, NoticeTestData.Maps, new ThrowingProbe());
        Assert.NotNull(scan);
    }

    sealed class ThrowingProbe : IFileProbe
    {
        public bool Exists(string path) => throw new IOException("disk gone");
        public bool IsAmpb(string path) => throw new IOException("disk gone");
    }

    // ---- the log fragments ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TheAvailabilityLineNamesTheStateOfEveryInput()
    {
        MapAvailabilityInput healthy = NoticeTestData.Healthy();
        Assert.Equal("availability: arena=verified, offered=4/4 [arena_equator,arena_bay5,arena_airpit,arena_bowl], files=ok, server=not_needed",
            MapNoticeInputs.DescribeAvailability(healthy));

        MapAvailabilityInput failing = NoticeTestData.Input(NoticeTestData.NotVerified(ArenaInstallCause.NotRegistered), null, hooks: new[] { "BundleHost" });
        Assert.Equal("availability: arena=not_verified:not_registered, offered=?/4, files=skipped, server=not_needed, skipped hooks=BundleHost",
            MapNoticeInputs.DescribeAvailability(failing));

        MapAvailabilityInput missing = NoticeTestData.Input(NoticeTestData.Verified(), NoticeTestData.Offered("arena_equator"),
            NoticeTestData.Ok(NoticeTestData.Status("not_verified", "not_registered")), new FileScan(1, new[] { "a", "b" }, new[] { "Equator" }));
        Assert.Equal("availability: arena=verified, offered=1/4 [arena_equator], files=2 missing, server=ok:not_verified/not_registered",
            MapNoticeInputs.DescribeAvailability(missing));
    }

    [Fact]
    public void TheShowingSummaryCarriesTheCauseCodes()
    {
        Assert.Equal("client=verified:none server=not_needed offered=4/4", MapNoticeInputs.DescribeSummary(NoticeTestData.Healthy()));
        Assert.Equal("client=not_verified:files_mismatch server=not_needed offered=?/4",
            MapNoticeInputs.DescribeSummary(NoticeTestData.Input(NoticeTestData.NotVerified(ArenaInstallCause.FilesMismatch), null)));
        Assert.Equal("client=verified:none server=404 offered=0/4",
            MapNoticeInputs.DescribeSummary(NoticeTestData.Input(NoticeTestData.Verified(), new List<OfferedLocation>(), new ProbeView(ProbeState.NotFound))));
    }

    [Theory]
    [InlineData(ProbeState.NotAsked, "not_needed")]
    [InlineData(ProbeState.Pending, "pending")]
    [InlineData(ProbeState.NotFound, "404")]
    [InlineData(ProbeState.Failed, "failed")]
    [InlineData(ProbeState.Unavailable, "no_http")]
    [InlineData(ProbeState.Unreadable, "unreadable")]
    public void TheProbeHasItsLogCodes(ProbeState state, string code)
    {
        Assert.Equal(code, new ProbeView(state).LogCode);
    }

    [Fact]
    public void AnAnsweredProbeNamesTheServersGate()
    {
        var view = NoticeTestData.Ok(NoticeTestData.Status("verified", "none"));
        Assert.Equal("ok:verified/none", view.LogCode);
    }
}
