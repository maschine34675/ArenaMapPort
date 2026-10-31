using System.Text.Json;
using System.Text.RegularExpressions;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// Native sea water for Bay5 (2026-09-28) and Iceberg (2026-10-31), docs/progress/bay5-water.md and iceberg-release.md "native sea water".
/// NativeWaterRepairs.cs (WaterSSR types, a coroutine) cannot run here, so the table it is driven by (NativeWaterModel, linked), the descriptor
/// flag that selects the maps, the facts of the source text (no map literal in the generic path, the lifecycle calls that make the placeholder come
/// back on teardown) and the client build's staging of both packages are pinned instead. The Python side pins the extractor, the validator and the packer.
/// </summary>
public sealed class NativeWaterTests
{
    static string Text(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return File.ReadAllText(path).Replace("\r\n", "\n");   // the sources are CRLF on the build machine
        }
        throw new FileNotFoundException(relative);
    }

    static string Between(string text, string from, string to)
    {
        int a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, "missing: " + from);
        int b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
        Assert.True(b > a, "missing: " + to);
        return text.Substring(a, b - a);
    }

    /// <summary>The code of a source file without its // and /// comments (nothing in the file puts // inside a string).</summary>
    static string Code(string text) => string.Join("\n", text.Split('\n').Select(line =>
    {
        int i = line.IndexOf("//", StringComparison.Ordinal);
        return i >= 0 ? line.Substring(0, i) : line;
    }));

    static readonly string[] WaterMaps = { "arena_bay5", "arena_iceberg" };

    [Fact]
    public void OnlyBay5AndIcebergHaveNativeWater_AndTheFlagMirrorsTheSidecars()
    {
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            using var sidecar = JsonDocument.Parse(Text("tools/maps/" + map.Key["arena_".Length..] + ".json"));
            bool flag = sidecar.RootElement.TryGetProperty("has_native_water", out var value) && value.GetBoolean();
            Assert.Equal(flag, map.HasNativeWater);
            Assert.Equal(WaterMaps.Contains(map.Key), map.HasNativeWater);
        }
        Assert.True(ArenaMapRegistry.Bay5.HasNativeWater);
        Assert.True(ArenaMapRegistry.Iceberg.HasNativeWater);
        Assert.False(ArenaMapRegistry.Equator.HasNativeWater);
        Assert.False(ArenaMapRegistry.AirPit.HasNativeWater);
        Assert.False(ArenaMapRegistry.Bowl.HasNativeWater);
    }

    [Fact]
    public void EveryMapWithNativeWaterHasExactlyOneSwitchRow_AndNoOtherMapHasOne()
    {
        Assert.Equal(WaterMaps.OrderBy(k => k), NativeWaterModel.Switches.Select(r => r.MapKey).OrderBy(k => k));
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            var row = NativeWaterModel.SwitchOf(map);
            if (map.HasNativeWater) Assert.Equal(map.Key, row!.MapKey);
            else Assert.Null(row);
        }
        Assert.Null(NativeWaterModel.SwitchOf(null!));
    }

    [Fact]
    public void Bay5KeepsItsPublishedKeyAndText()
    {
        // 2026-09-28: a saved value of this key keeps applying; the key, the section and the text never change.
        var map = ArenaMapRegistry.Bay5;
        var row = NativeWaterModel.SwitchOf(map)!;
        Assert.Equal("Water (arena_bay5)", NativeWaterModel.SectionOf(map));
        Assert.Equal("NativeHarborWater", row.Key);
        Assert.Equal("Use original harbor waves, foam and depth settings with EFT's native water renderer. " +
                     "Responds to world lighting at day and night. Live toggle; off restores the old placeholder.", row.Description);
    }

    [Fact]
    public void IcebergHasItsOwnKeyInItsOwnSection_AndNeitherCollidesWithThePuddleSwitch()
    {
        var map = ArenaMapRegistry.Iceberg;
        var row = NativeWaterModel.SwitchOf(map)!;
        Assert.Equal("Water (arena_iceberg)", NativeWaterModel.SectionOf(map));
        Assert.Equal("NativeSeaWater", row.Key);
        Assert.Contains("Sea/Plane", row.Description);
        Assert.EndsWith("Live toggle; off restores the old placeholder.", row.Description);
        var keys = NativeWaterModel.Switches.Select(r => r.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.DoesNotContain("PuddleFootsteps", keys);      // the puddle switch of PuddleFootstepRepairs sits in the same "Water (<key>)" section
        Assert.All(NativeWaterModel.Switches, r => Assert.NotEmpty(r.Description));
    }

    [Fact]
    public void ThePackageLivesBelowThePluginDirectoryUnderTheMapKey()
    {
        foreach (var map in new[] { ArenaMapRegistry.Bay5, ArenaMapRegistry.Iceberg })
            Assert.Equal(Path.Combine("plugin", "water", map.Key), NativeWaterModel.PackageDirectory("plugin", map));
    }

    [Theory]
    [InlineData(1, "arena_bay5", "Arena_Bay5_Main", null)]
    [InlineData(1, "arena_iceberg", "Arena_Sanatorium", null)]
    [InlineData(2, "arena_iceberg", "Arena_Sanatorium", "Unexpected water package identity")]
    [InlineData(0, "arena_bay5", "Arena_Bay5_Main", "Unexpected water package identity")]
    [InlineData(1, "arena_bay5", "Arena_Sanatorium", "Water target outside arena_bay5")]
    [InlineData(1, "arena_iceberg", "Arena_Bay5_Main", "Water target outside arena_iceberg")]
    [InlineData(1, "arena_iceberg", "Arena_Iceberg_Scripts", null)]
    [InlineData(1, "arena_iceberg", "", "Water target outside arena_iceberg")]
    public void WaterJsonIdentity_IsSchema1_TheMapsOwnKey_AndASceneOfTheMap(int schema, string packageKey, string scene, string? error)
    {
        // The package of one map presented to the other map (a copied water directory) is refused, whichever way round.
        var map = packageKey == "arena_bay5" ? ArenaMapRegistry.Bay5 : ArenaMapRegistry.Iceberg;
        Assert.Equal(error, NativeWaterModel.IdentityError(schema, packageKey, scene, map));
        var other = map == ArenaMapRegistry.Bay5 ? ArenaMapRegistry.Iceberg : ArenaMapRegistry.Bay5;
        Assert.Equal("Unexpected water package identity", NativeWaterModel.IdentityError(schema, packageKey, scene, other));
    }

    [Theory]
    [InlineData("textures/foam.bin", true)]
    [InlineData("textures/rippleTexture.bin", true)]
    [InlineData("textures/normalsDetails.bin", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("textures/", false)]
    [InlineData("foam.bin", false)]
    [InlineData("other/foam.bin", false)]
    [InlineData("textures/../foam.bin", false)]
    [InlineData("textures/..", false)]
    [InlineData("textures/a/b.bin", false)]
    [InlineData("textures\\foam.bin", false)]
    [InlineData("textures/a\\b.bin", false)]
    public void TextureEntriesAreBareNamesDirectlyBelowTextures(string? file, bool ok)
    {
        Assert.Equal(ok, NativeWaterModel.IsTextureFileName(file!));
    }

    [Fact]
    public void TheGenericRepairNamesNoMapAndKeepsItsLifecycleCalls()
    {
        var source = Text("ArenaMapPort.Client/NativeWaterRepairs.cs");
        var code = Code(source);
        foreach (var literal in new[] { "Bay5", "bay5", "Iceberg", "iceberg", "NativeHarborWater", "NativeSeaWater", "\"arena_", "\"Water (", "Arena_" })
            Assert.DoesNotContain(literal, code);
        // the maps come from the descriptor and the Scripts scene that loaded; the switch rows from the model
        Assert.Contains("foreach (var map in ArenaMapRegistry.All)", code);
        Assert.Contains("NativeWaterModel.SwitchOf(map)", code);
        Assert.Contains("ArenaMapRaid.MapForScriptsScene(scene.name)", code);
        Assert.Contains("Plugin.Instance.Settings.Bind(NativeWaterModel.SectionOf(map), row.Key, true, row.Description)", code);
        Assert.DoesNotContain("Plugin.Instance.Config", code);
        Assert.Contains("NativeWaterModel.IdentityError(", code);
        Assert.Contains("NativeWaterModel.IsTextureFileName(file)", code);
        Assert.Contains("NativeWaterModel.PackageDirectory(", code);
        Assert.Contains("Map.AllSceneNames", code);
        // the behaviour of the harbor: one forced-off draw of the placeholder, the snow-mask state restored, the old materials back, in Stop()
        Assert.Single(Regex.Matches(code, @"_target\.forceRenderingOff = true;"));
        Assert.Single(Regex.Matches(code, @"WaterRendererv3\.DisableSnowMask\(true\);"));
        var stop = Between(code, "void Stop()", "void OnDisable()");
        Assert.Contains("_target.sharedMaterials = _oldMaterials;", stop);
        Assert.Contains("_target.forceRenderingOff = _oldForceOff;", stop);
        Assert.Contains("WaterRendererv3.DisableSnowMask(_oldSnowDisabled);", stop);
        // one scan per start: the only scene search is the single renderer check of StartWater, never the polling loop
        var run = Between(code, "internal IEnumerator Run()", "void StartWater()");
        Assert.DoesNotContain("FindObject", run);
        Assert.DoesNotContain("GetComponentsInChildren", code);
        Assert.Single(Regex.Matches(code, @"FindObjectOfType<WaterRendererv3>\(\)"));
        // the log line of the host check names the map and the plane
        Assert.Contains("enabled: map={Map.Key} target={sceneName}/{path}", code);
        Assert.Contains("worldY={tr.position.y:0.00} extent={bounds.size.x:0}x{bounds.size.z:0} m", code);
    }

    [Fact]
    public void ThePluginInstallsTheNativeWaterAfterThePlaceholderThatItReplaces()
    {
        var plugin = Code(Text("ArenaMapPort.Client/Plugin.cs"));
        int placeholder = plugin.IndexOf("SeaPlaceholderRepairs.Install();", StringComparison.Ordinal);
        int native = plugin.IndexOf("NativeWaterRepairs.Install();", StringComparison.Ordinal);
        Assert.True(placeholder > 0 && native > placeholder);
        Assert.Single(Regex.Matches(plugin, @"NativeWaterRepairs\.Install\(\);"));
    }

    [Fact]
    public void TheClientBuildStagesBothPackagesInReleaseAndDllOnlyBuilds()
    {
        var csproj = Text("ArenaMapPort.Client/ArenaMapPort.Client.csproj");
        int start = csproj.IndexOf("<Target Name=\"ArenaMapPortStageWater\"", StringComparison.Ordinal);
        Assert.True(start > 0);
        var target = csproj.Substring(start, csproj.IndexOf("</Target>", csproj.IndexOf("<Copy SourceFiles=\"@(ArenaMapPortWaterFile)\"", start, StringComparison.Ordinal), StringComparison.Ordinal) - start);
        // the header: after the dist copy, Release only, no DllOnly exemption (the package is a hard input of every client build)
        var header = target.Substring(0, target.IndexOf('>') + 1);
        Assert.Contains("AfterTargets=\"ArenaMapPortDistCopy\"", header);
        Assert.Contains("Condition=\"'$(Configuration)'=='Release'\"", header);
        Assert.DoesNotContain("DllOnly", target);
        // one explicit file list per map: water.json and the four texture mip chains, each a missing-input Error, copied to water\<key>\<Dest>
        var textures = new[] { "foam", "normals", "normalsDetails", "rippleTexture" };
        foreach (var (key, folder) in new[] { ("arena_bay5", "bay5-water"), ("arena_iceberg", "iceberg-water") })
        {
            Assert.Single(Regex.Matches(target, Regex.Escape($@"\{folder}\water.json""><MapKey>{key}</MapKey><Dest>water.json</Dest>")));
            foreach (var texture in textures)
                Assert.Single(Regex.Matches(target, Regex.Escape($@"\{folder}\textures\{texture}.bin""><MapKey>{key}</MapKey><Dest>textures\{texture}.bin</Dest>")));
        }
        Assert.Equal(10, Regex.Matches(target, @"<ArenaMapPortWaterFile Include=").Count);
        Assert.Contains("<Error Condition=\"!Exists('%(ArenaMapPortWaterFile.Identity)')\" Text=\"Missing native water input %(ArenaMapPortWaterFile.Identity); run tools/extract/extract_water.py --map %(ArenaMapPortWaterFile.MapKey)\" />", target);
        Assert.Contains("DestinationFiles=\"@(ArenaMapPortWaterFile->'$(ArenaMapPortDistDir)\\water\\%(MapKey)\\%(Dest)')\"", target);
        Assert.DoesNotContain("airpit", target.ToLowerInvariant());
        Assert.DoesNotContain("equator", target.ToLowerInvariant());
        Assert.DoesNotContain("bowl", target.ToLowerInvariant());
    }
}
