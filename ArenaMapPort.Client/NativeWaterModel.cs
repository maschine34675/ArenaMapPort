using System;
using System.IO;
using ArenaMapPort.Shared;

#nullable disable

namespace ArenaMapPort.Client
{
    /// <summary>
    /// The Unity-free rules of <see cref="NativeWaterRepairs"/> (linked into the server tests): which maps have a native sea-water package, the
    /// developer switch of each map (section, key, text), where the package lives and what its water.json has to say about itself. The repair
    /// itself stays map-agnostic; everything that differs between the maps is a row of <see cref="Switches"/> or a field of the descriptor
    /// (<see cref="ArenaMapDescriptor.HasNativeWater"/>, mirrored in tools/maps/&lt;stem&gt;.json has_native_water).
    ///
    /// The package is tools/extract/extract_water.py's output (water.json plus the four raw texture mip chains) staged to
    /// &lt;plugin&gt;/water/&lt;map key&gt;/ by the client build; the map's one empty-material Sea/Plane mesh is the surface (Bay5: the harbor, Iceberg: the sea
    /// below the canyon). Both maps carry the very same container settings and textures from Arena's shared sea prefab, so the two packages differ in
    /// identity only. See docs/progress/bay5-water.md and docs/progress/iceberg-release.md ("native sea water").
    /// </summary>
    internal static class NativeWaterModel
    {
        internal const string Tag = "[Water]";
        /// <summary>Folder below the plugin directory that holds one sub folder per map key.</summary>
        internal const string PackageFolder = "water";
        internal const string PackageFile = "water.json";
        internal const string TextureFolderPrefix = "textures/";
        internal const int SchemaVersion = 1;
        internal const string ShaderName = "Hidden/WaterForSSR";

        /// <summary>One map's developer switch: the BepInEx key (in section "Water (&lt;map key&gt;)") and the text of its tooltip.</summary>
        internal sealed class MapSwitch
        {
            internal MapSwitch(string mapKey, string key, string description)
            {
                MapKey = mapKey;
                Key = key;
                Description = description;
            }

            internal string MapKey { get; }
            internal string Key { get; }
            internal string Description { get; }
        }

        /// <summary>
        /// One row per map with <see cref="ArenaMapDescriptor.HasNativeWater"/>; NativeWaterTests pins that both sets are equal. Bay5's key and text are
        /// the ones of 2026-09-28 (a saved value keeps applying, a changed key would silently re-seed the default), so they never change; Iceberg's
        /// own key joined on 2026-10-31. Both default to true and are live: off restores the placeholder renderer.
        /// </summary>
        internal static readonly MapSwitch[] Switches =
        {
            new MapSwitch(ArenaMapRegistry.Bay5.Key, "NativeHarborWater",
                "Use original harbor waves, foam and depth settings with EFT's native water renderer. " +
                "Responds to world lighting at day and night. Live toggle; off restores the old placeholder."),
            new MapSwitch(ArenaMapRegistry.Iceberg.Key, "NativeSeaWater",
                "Use the original sea waves, foam and depth settings with EFT's native water renderer on the sea below the canyon (Sea/Plane). " +
                "Responds to world lighting at day and night. Live toggle; off restores the old placeholder."),
        };

        /// <summary>The switch row of a map, or null for a map without native water.</summary>
        internal static MapSwitch SwitchOf(ArenaMapDescriptor map)
        {
            if (map == null || !map.HasNativeWater)
            {
                return null;
            }
            foreach (var row in Switches)
            {
                if (row.MapKey == map.Key)
                {
                    return row;
                }
            }
            return null;
        }

        /// <summary>The BepInEx section of a map's water keys; the puddle footstep switch of the same map lives in it too.</summary>
        internal static string SectionOf(ArenaMapDescriptor map) => "Water (" + map.Key + ")";

        /// <summary>&lt;plugin&gt;/water/&lt;map key&gt;.</summary>
        internal static string PackageDirectory(string pluginDirectory, ArenaMapDescriptor map) =>
            Path.Combine(pluginDirectory, PackageFolder, map.Key);

        /// <summary>
        /// Null when water.json's identity fits the map (schema, own key, a target scene that belongs to the map); otherwise the reason. The package is
        /// BSG-derived data staged beside the DLL: a file of another map, an older schema or a scene the map never loads is refused, and the placeholder stays.
        /// </summary>
        internal static string IdentityError(int schemaVersion, string mapKey, string sceneName, ArenaMapDescriptor map)
        {
            if (schemaVersion != SchemaVersion || mapKey != map.Key)
            {
                return "Unexpected water package identity";
            }
            if (Array.IndexOf(map.AllSceneNames, sceneName) < 0)
            {
                return "Water target outside " + map.Key;
            }
            return null;
        }

        /// <summary>A texture entry's file must be a bare name directly below textures/ (nothing that climbs out of the package directory).</summary>
        internal static bool IsTextureFileName(string file)
        {
            if (string.IsNullOrEmpty(file) || !file.StartsWith(TextureFolderPrefix, StringComparison.Ordinal) || file.Length == TextureFolderPrefix.Length)
            {
                return false;
            }
            string name = file.Substring(TextureFolderPrefix.Length);
            return name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 && name != "." && name != ".." && Path.GetFileName(file) == name;
        }
    }
}
