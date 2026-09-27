using System;
using System.Collections.Generic;
using System.IO;
using ArenaMapPort.Server;
using ArenaMapPort.Shared;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// PLAN-TWO-MAPS item 10 (docs/progress/two-maps-item-10-banners.md) — exercises
/// <see cref="ArenaMapBanners"/> without a live server or any BSG art on disk: every test below
/// only ever touches an empty <see cref="System.IO.Path.GetTempPath"/> subdirectory it creates and
/// deletes itself, standing in for the extracted banner image
/// (tools/extract/extract_banners.py's own output, never checked in).
///
/// The one invariant every test here ultimately protects is AGENTS.md's documented landmine: a
/// <c>Banners</c> entry whose backing file does not exist must never be produced — that would
/// resolve, client-side, to an "[UNHANDLED]" server log line and an empty error dialog at the
/// location-select screen instead of just falling back to the default art.
/// </summary>
public sealed class ArenaMapBannersTests : IDisposable
{
    private readonly string _bannersDir;

    public ArenaMapBannersTests()
    {
        _bannersDir = Path.Combine(Path.GetTempPath(), "ArenaMapBannersTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_bannersDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_bannersDir))
        {
            Directory.Delete(_bannersDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("arena_equator", "arena_equator_banner", "arena_equator")]
    [InlineData("arena_bay5", "arena_bay5_banner", "arena_bay5")]
    public void For_ReturnsTheExpectedDescriptor_ForEachBanneredMap(string mapKey, string expectedId, string expectedFileStem)
    {
        var map = FindMap(mapKey);

        var descriptor = ArenaMapBanners.For(map);

        Assert.NotNull(descriptor);
        Assert.Equal(expectedId, descriptor!.Id);
        Assert.Equal(expectedFileStem, descriptor.FileStem);
    }

    [Theory]
    [MemberData(nameof(MapsWithoutABanner))]
    public void For_ReturnsNull_ForEveryOtherMap(ArenaMapDescriptor map)
    {
        Assert.Null(ArenaMapBanners.For(map));
    }

    public static IEnumerable<object[]> MapsWithoutABanner()
    {
        foreach (var map in ArenaMapRegistry.All)
        {
            if (map.Key != ArenaMapRegistry.Equator.Key && map.Key != ArenaMapRegistry.Bay5.Key)
            {
                yield return new object[] { map };
            }
        }
    }

    [Fact]
    public void Resolve_ReturnsNull_AndWarns_ForAMapWithNoBannerDescriptor()
    {
        var warnings = new List<string>();

        var resolved = ArenaMapBanners.Resolve(ArenaMapRegistry.AirPit, _bannersDir, warnings.Add);

        Assert.Null(resolved);
        // AirPit has no banner descriptor at all -- Resolve must not warn about a "missing file"
        // for a map that was never supposed to have one.
        Assert.Empty(warnings);
    }

    [Fact]
    public void Resolve_ReturnsNull_AndWarnsExactlyOnce_WhenNoCandidateFileExists()
    {
        var warnings = new List<string>();

        var resolved = ArenaMapBanners.Resolve(ArenaMapRegistry.Equator, _bannersDir, warnings.Add);

        Assert.Null(resolved);
        Assert.Single(warnings);
        Assert.Contains("arena_equator", warnings[0]);
    }

    [Fact]
    public void Resolve_NeverReturnsABanner_WhenTheFileIsAbsent_RegardlessOfExtensionTried()
    {
        // Guards the landmine directly: an empty banners directory must resolve to null for both
        // maps, for every extension Resolve knows how to look for -- never a Banner pointing at a
        // file that isn't there.
        foreach (var map in new[] { ArenaMapRegistry.Equator, ArenaMapRegistry.Bay5 })
        {
            var resolved = ArenaMapBanners.Resolve(map, _bannersDir, _ => { });
            Assert.Null(resolved);
        }
    }

    [Fact]
    public void Resolve_FindsAJpgFile_AndBuildsTheExpectedBannerAndRoute()
    {
        File.WriteAllBytes(Path.Combine(_bannersDir, "arena_equator.jpg"), new byte[] { 1, 2, 3 });

        var resolved = ArenaMapBanners.Resolve(ArenaMapRegistry.Equator, _bannersDir, _ => Assert.Fail("should not warn on a hit"));

        Assert.NotNull(resolved);
        var value = resolved!.Value;
        Assert.Equal("arena_equator_banner", value.Banner.Id);
        Assert.NotNull(value.Banner.Picture);
        Assert.Equal("arena_equator.jpg", value.Banner.Picture!.File);
        Assert.Equal("banners/arena_equator.jpg", value.Banner.Picture.Path);
        Assert.Equal("", value.Banner.Picture.Rcid);
        Assert.Equal("banners", value.Banner.Picture.Type);
        // The registered ImageRouter key must be extension-FREE (see ArenaMapBanners.Resolve's own
        // comment: AddRoute never strips one, but CanHandle/HandleAsync strip the extension off
        // the incoming request before comparing) -- unlike Picture.Path/pic.path above, which DOES
        // carry the real extension (that's what the client's "/files/"+pic.path request sends).
        Assert.Equal("/files/banners/arena_equator", value.RouteKey);
        Assert.Equal(Path.Combine(_bannersDir, "arena_equator.jpg"), value.FilePath);
    }

    [Fact]
    public void Resolve_FindsAPngFile_WhenNoJpgExists()
    {
        File.WriteAllBytes(Path.Combine(_bannersDir, "arena_bay5.png"), new byte[] { 1, 2, 3 });

        var resolved = ArenaMapBanners.Resolve(ArenaMapRegistry.Bay5, _bannersDir, _ => { });

        Assert.NotNull(resolved);
        Assert.Equal("arena_bay5.png", resolved!.Value.Banner.Picture!.File);
        // Route key stays extension-free regardless of which candidate extension was found.
        Assert.Equal("/files/banners/arena_bay5", resolved.Value.RouteKey);
    }

    [Fact]
    public void Resolve_RouteKey_IsAlwaysExtensionFree_UnlikePicPath()
    {
        // Regression guard: ImageRouter.AddRoute stores its key exactly as given (no extension
        // stripping), but CanHandle/HandleAsync strip the extension off the INCOMING request
        // before looking it up -- so a RouteKey that (incorrectly) carried the file extension
        // would never match a real client request and this banner would silently fail to load
        // (the exact [UNHANDLED] landmine this whole feature exists to avoid). Picture.Path (what
        // the client's request URL is built from) must still carry the real extension.
        File.WriteAllBytes(Path.Combine(_bannersDir, "arena_equator.jpg"), new byte[] { 1 });

        var resolved = ArenaMapBanners.Resolve(ArenaMapRegistry.Equator, _bannersDir, _ => { });

        Assert.DoesNotContain(".", resolved!.Value.RouteKey);
        Assert.Contains(".", resolved.Value.Banner.Picture!.Path);
    }

    [Fact]
    public void Resolve_PrefersJpgOverPng_WhenBothExist()
    {
        File.WriteAllBytes(Path.Combine(_bannersDir, "arena_equator.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_bannersDir, "arena_equator.png"), new byte[] { 2 });

        var resolved = ArenaMapBanners.Resolve(ArenaMapRegistry.Equator, _bannersDir, _ => { });

        Assert.Equal("arena_equator.jpg", resolved!.Value.Banner.Picture!.File);
    }

    [Fact]
    public void Resolve_DoesNotConfuseTheTwoMaps_WhenOnlyOnesFileExists()
    {
        File.WriteAllBytes(Path.Combine(_bannersDir, "arena_equator.jpg"), new byte[] { 1 });

        Assert.NotNull(ArenaMapBanners.Resolve(ArenaMapRegistry.Equator, _bannersDir, _ => { }));
        Assert.Null(ArenaMapBanners.Resolve(ArenaMapRegistry.Bay5, _bannersDir, _ => { }));
    }

    [Fact]
    public void ApplyLocale_WritesBothKeys_ForABanneredMap()
    {
        var locale = new GlobalLocaleDictionary();

        var result = ArenaMapBanners.ApplyLocale(locale, ArenaMapRegistry.Equator);

        Assert.Same(locale, result);
        Assert.True(locale.ContainsKey("arena_equator_banner Name"));
        Assert.True(locale.ContainsKey("arena_equator_banner Description"));
        Assert.Equal(ArenaMapRegistry.Equator.LocaleName, locale["arena_equator_banner Name"]);
        Assert.Equal(ArenaMapRegistry.Equator.LocaleDescription, locale["arena_equator_banner Description"]);
    }

    [Fact]
    public void ApplyLocale_DoesNotOverwriteAnExistingKey()
    {
        var locale = new GlobalLocaleDictionary { ["arena_equator_banner Name"] = "custom translation" };

        ArenaMapBanners.ApplyLocale(locale, ArenaMapRegistry.Equator);

        Assert.Equal("custom translation", locale["arena_equator_banner Name"]);
    }

    [Fact]
    public void ApplyLocale_IsANoOp_ForAMapWithNoBannerDescriptor()
    {
        var locale = new GlobalLocaleDictionary();

        ArenaMapBanners.ApplyLocale(locale, ArenaMapRegistry.AirPit);

        Assert.Empty(locale);
    }

    [Fact]
    public void ApplyLocale_ReturnsNull_WhenGivenNull()
    {
        Assert.Null(ArenaMapBanners.ApplyLocale(null, ArenaMapRegistry.Equator));
    }

    private static ArenaMapDescriptor FindMap(string key)
    {
        foreach (var map in ArenaMapRegistry.All)
        {
            if (map.Key == key)
            {
                return map;
            }
        }
        throw new InvalidOperationException($"no registered map with key {key}");
    }
}
