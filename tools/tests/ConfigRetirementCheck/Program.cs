using ArenaMapPort.Client;
using BepInEx.Configuration;

CompactChecks.Run();

// Exercise real BepInEx load/bind/save/reload, using only an isolated temporary file.
string path = Path.Combine(Path.GetTempPath(), $"ArenaMapPort-config-check-{Guid.NewGuid():N}.cfg");
try
{
    File.WriteAllText(path, """
        [Logging]
        Verbose = true
        [Lighting (arena_equator)]
        CoverMinHeight = 9
        AuthoredRainCover = false
        AmbientIntensity = 0.25
        DefaultsRevision = 4
        VertexGroundDiffuseMultiplier = 2
        SunIntensityMultiplier = 0.72
        [Lighting (arena_bay5)]
        SunIntensityMultiplier = 6
        SunColorWhiteBlend = 0.8
        VertexGroundDiffuseMultiplier = 1.4
        [LightingLab (arena_equator)]
        AmbientCustomIntensity = 3
        [LightingLab (arena_bay5)]
        SunIntensityMultiplier = 0.6
        [Lighting (arena_bowl)]
        ReflectionIntensity = 0.43
        [Future category]
        PreserveMe = future-value
        """);
    var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
    var ambient = config.Bind("Lighting (arena_equator)", "AmbientIntensity", 0.85f);
    var revision = config.Bind("Lighting (arena_equator)", "DefaultsRevision", 0);
    // A live entry must win even if its name is on the retired list.
    var live = config.Bind("Lighting (arena_equator)", "CoverMinHeight", 2.2f);
    Check(RetiredConfiguration.RemoveSavedEntries(config) == 6, "Exactly six unbound retired keys must be removed");
    Check(RetiredConfiguration.RemoveSavedEntries(config) == 0, "Cleanup must be idempotent");
    Check(ambient.Value == 0.25f && revision.Value == 4 && live.Value == 9f, "Bound values and migration state changed");
    var reload = new ConfigFile(path, false) { SaveOnConfigSet = false };
    Check(reload.Bind("Lighting (arena_equator)", "AmbientIntensity", -1f).Value == 0.25f, "Saved tuning lost");
    Check(reload.Bind("Lighting (arena_equator)", "DefaultsRevision", -1).Value == 4, "Saved migration marker lost");
    Check(reload.Bind("Lighting (arena_equator)", "SunIntensityMultiplier", -1f).Value == 0.72f, "Equator sun lost");
    Check(reload.Bind("Lighting (arena_bay5)", "VertexGroundDiffuseMultiplier", -1f).Value == 1.4f, "Bay5 ground lost");
    Check(reload.Bind("LightingLab (arena_bay5)", "SunIntensityMultiplier", -1f).Value == 0.6f, "Real sun lost");
    Check(reload.Bind("Lighting (arena_bowl)", "ReflectionIntensity", -1f).Value == 0.43f, "Hidden map tuning lost");
    Check(reload.Bind("Future category", "PreserveMe", "missing").Value == "future-value", "Unknown orphan lost");
    Check(reload.Bind("Logging", "Verbose", false).Value == false, "Retired key survived save/reload");
    // Exercise both an untouched old configuration and saved custom mix through actual
    // BepInEx serialization, then verify the migration is one-shot across process restarts.
    foreach (var map in new[] { "arena_equator", "arena_bay5" })
    {
        string section = "Audio (" + map + ")";
        var gain = reload.Bind(section, "AmbientGainDb", 0f);
        var offset = reload.Bind(section, "AmbientVolumeOffset", 0f);
        var range = reload.Bind(section, "AmbientRangeMultiplier", 1f);
        AmbientAudioDefaults.Migrate(reload, map, gain, offset, range);
        Check(gain.Value == (map == "arena_equator" ? 5f : 4f), "Accepted gain missing");
        Check(offset.Value == .4f, "Accepted offset missing");
        Check(range.Value == (map == "arena_equator" ? 2.5f : 4f), "Accepted range missing");
        gain.Value = 0; offset.Value = 0; range.Value = 1;
    }
    // AirPit's ambience keys are new (PROVISIONAL mix, 2026-10-12): they bind with the provisional defaults, and no
    // legacy migration step may touch any AirPit mix, a deliberately neutral one included.
    {
        const string airpit = "Audio (arena_airpit)";
        AmbientAudioDefaults.Values("arena_airpit", out var airpitGain, out var airpitOffset, out var airpitRange);
        var gain = reload.Bind(airpit, "AmbientGainDb", airpitGain);
        var offset = reload.Bind(airpit, "AmbientVolumeOffset", airpitOffset);
        var range = reload.Bind(airpit, "AmbientRangeMultiplier", airpitRange);
        AmbientAudioDefaults.Migrate(reload, "arena_airpit", gain, offset, range);
        Check(gain.Value == 4f && offset.Value == .4f && range.Value == 3f, "AirPit provisional mix missing");
        gain.Value = 0; offset.Value = 0; range.Value = 1;
        // The first Migrate stamped revision 2, which alone would make the second call return early. Reset the marker so
        // the legacy-mix steps are reachable; only the map's "no legacy mix" rule can then keep the neutral mix neutral.
        reload.Bind(airpit, "AmbientDefaultsRevision", 0).Value = 0;
        AmbientAudioDefaults.Migrate(reload, "arena_airpit", gain, offset, range);
        Check(gain.Value == 0 && offset.Value == 0 && range.Value == 1, "AirPit neutral mix was migrated");
    }
    DeveloperOptions.Install(reload);
    Check(!DeveloperOptions.Enabled, "Developer mode must default off");
    reload.Bind("General", "DeveloperMode", false).Value = true;
    Check(!DeveloperOptions.Enabled, "Developer mode changed before restart");
    reload.Save();
    var second = new ConfigFile(path, false) { SaveOnConfigSet = false };
    DeveloperOptions.Install(second);
    Check(DeveloperOptions.Enabled, "Developer mode did not survive restart");
    foreach (var map in new[] { "arena_equator", "arena_bay5", "arena_airpit" })
    {
        string section = "Audio (" + map + ")";
        AmbientAudioDefaults.Values(map, out var gainDefault, out var offsetDefault, out var rangeDefault);
        var gain = second.Bind(section, "AmbientGainDb", gainDefault);
        var offset = second.Bind(section, "AmbientVolumeOffset", offsetDefault);
        var range = second.Bind(section, "AmbientRangeMultiplier", rangeDefault);
        AmbientAudioDefaults.Migrate(second, map, gain, offset, range);
        Check(gain.Value == 0 && offset.Value == 0 && range.Value == 1, "Repeat migration overwrote a custom mix");
    }
    Console.WriteLine("RELEASE_CONFIG_PERSISTENCE_OK: accepted defaults, one-shot migration, restart-latched developer mode");
    Console.WriteLine("CONFIG_RETIREMENT_OK: scoped removal, saved values, unknown orphans, live entries and repeat cleanup");
}
finally
{
    File.Delete(path);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
