using System.Text.Json;
using ArenaMapPort.Client;
using ArenaMapPort.Shared;
using Xunit;

namespace ArenaMapPort.Server.Tests;

/// <summary>
/// The emission cap exists twice, in tools/maps/&lt;stem&gt;.json (emission_cap, absent = 0) and in the descriptor (EmissionCap); the validator
/// (validate_map_descriptors.py) and these tests hold them equal. Only Iceberg has one: the four released maps keep the repair off.
/// </summary>
public sealed class EmissionCapDescriptorTests
{
    private static JsonDocument Sidecar(ArenaMapDescriptor map)
    {
        var name = map.Key["arena_".Length..] + ".json";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "maps", name);
            if (File.Exists(candidate)) return JsonDocument.Parse(File.ReadAllText(candidate));
        }
        throw new FileNotFoundException(name);
    }

    [Fact]
    public void EveryDescriptorMirrorsItsSidecarsCapAndOnlyIcebergHasOne()
    {
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            using var document = Sidecar(map);
            var sidecar = document.RootElement.TryGetProperty("emission_cap", out var cap) ? (float)cap.GetDouble() : 0f;
            Assert.True(sidecar == map.EmissionCap, $"{map.Key}: sidecar {sidecar} vs descriptor {map.EmissionCap}");
            Assert.InRange(map.EmissionCap, 0f, EmissionCapModel.MaxCap);
            Assert.Equal(map == ArenaMapRegistry.Iceberg, map.EmissionCap > 0f);
        }
    }

    [Fact]
    public void EveryDescriptorMirrorsItsSidecarsFloorAndOnlyIcebergHasOne()
    {
        foreach (var map in ArenaMapRegistry.Catalog)
        {
            using var document = Sidecar(map);
            var sidecar = document.RootElement.TryGetProperty("emission_floor", out var floor) ? (float)floor.GetDouble() : 0f;
            Assert.True(sidecar == map.EmissionFloor, $"{map.Key}: sidecar {sidecar} vs descriptor {map.EmissionFloor}");
            Assert.InRange(map.EmissionFloor, 0f, EmissionCapModel.MaxCap);
            Assert.Equal(map == ArenaMapRegistry.Iceberg, map.EmissionFloor > 0f);
            // a floor sits inside its map's band: not above the cap the same map carries
            if (map.EmissionCap > 0f) Assert.True(map.EmissionFloor <= map.EmissionCap, map.Key);
        }
        Assert.Equal(3.0f, ArenaMapRegistry.Iceberg.EmissionFloor);
    }

    [Fact]
    public void IcebergsCapIsFourAndTheReleasedMapsAreOff()
    {
        Assert.Equal(4.0f, ArenaMapRegistry.Iceberg.EmissionCap);
        foreach (var map in new[] { ArenaMapRegistry.Equator, ArenaMapRegistry.Bay5, ArenaMapRegistry.AirPit, ArenaMapRegistry.Bowl })
            Assert.Equal(0f, map.EmissionCap);
        // the four released maps' sidecars carry no key at all: their files are byte-identical to the release
        foreach (var map in new[] { ArenaMapRegistry.Equator, ArenaMapRegistry.Bay5, ArenaMapRegistry.AirPit, ArenaMapRegistry.Bowl })
        {
            using var document = Sidecar(map);
            Assert.False(document.RootElement.TryGetProperty("emission_cap", out _), map.Key);
        }
    }
}
