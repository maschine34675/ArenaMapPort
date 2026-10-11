using ArenaMapPort.Shared;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// First-run (code) defaults of the per-map lighting sections, kept in one pure table so the values can
    /// be pinned by the offline unit tests. WeatherRig.BindConfig reads every map-dependent default of the
    /// rig-related <c>Lighting (&lt;key&gt;)</c> keys from <see cref="Rig"/>; ArenaLightingRepairs.BindConfig
    /// asks <see cref="BindsAuthoredSunKeys"/> whether the map has a sun to scale. Nothing here touches Unity,
    /// EFT or BepInEx, so the test project links this file as is.
    ///
    /// Vanilla EFT has no such table: its maps carry their own sky, sun and ambient. A ported Arena map has
    /// none of them (its materials are p0 deferred shaders that expect the ambient a donor weather rig
    /// feeds), so each released map selects the Reserve_Base_Scripts rig. What differs per map is only the
    /// geometry-relative tuning (ZeroLevel) and the values tuned on the host for that map.
    ///
    /// Persistence rule (AGENTS.md): BepInEx saves every bound value, so an existing key's default must
    /// never change silently. Equator and Bay5 therefore have pinned rows (WeatherRigDefaultsTests) and
    /// Equator's later changes live in the DefaultsRevision migrations of WeatherRig. A map that was never
    /// released with saved keys, like AirPit (grep of both test installs, 2026-10-12: no "arena_airpit"
    /// section), needs no migration for its first set of defaults.
    ///
    /// Host evidence: Equator (docs/progress/donor-settings-camera-weather.md section 20), Bay5 (sections
    /// 12-19), AirPit rows are first-guess values until the AirPit host raid (docs/progress/airpit-release.md,
    /// item 10).
    /// </summary>
    internal static class MapLightingDefaults
    {
        internal const string OffDonor = "Off";
        internal const string ReserveDonor = "Reserve_Base_Scripts";

        /// <summary>The map-dependent default values of one rig section.</summary>
        internal sealed class RigValues
        {
            internal string Donor = OffDonor;
            /// <summary>Geometry-relative: roughly 4.4 m below the walkable ground (Bay5's spawn hall floor is at y -3.8).</summary>
            internal float ZeroLevel;
            internal float HeightFalloff;
            /// <summary>true = TextureBudgetTier.ReserveTier, false = Stub.</summary>
            internal bool ReserveTextureBudget;
            internal bool UseDonorCameraPrefab;
            internal float ReflectionIntensity;
            internal float AmbientIntensity;
            internal float IndoorFloorSmoothness;
            internal int IndoorCoverPercent;
            internal float IndoorReflectionStrength;
            internal bool IndoorSurfaceRepair;
            internal float ReflectiveSpecularCap;
        }

        /// <summary>
        /// Defaults for <c>Lighting (&lt;mapKey&gt;)</c>. Rig-active maps (Equator, Bay5, AirPit) get the
        /// Reserve donor set; every other key gets Donor "Off" with neutral values, a complete no-op.
        /// The tuned values below the donor set are map-specific on purpose: Equator's were accepted on the
        /// host (ambient 0.5, reflection 0.85, indoor reflection 0.95, specular cap 0.3, cover 30 percent for
        /// its open atrium lattice), Bay5 alone enables the mixed-mesh indoor surface repair. AirPit starts
        /// from the neutral values and is tuned on its own host raid.
        /// </summary>
        internal static RigValues Rig(string mapKey)
        {
            bool bay5 = mapKey == ArenaMapRegistry.Bay5.Key;
            bool equator = mapKey == ArenaMapRegistry.Equator.Key;
            bool airPit = mapKey == ArenaMapRegistry.AirPit.Key;
            bool rig = bay5 || equator || airPit;
            return new RigValues
            {
                Donor = rig ? ReserveDonor : OffDonor,
                // Equator's spawn markers sit at y 0.03..0.12 -> -4.4; AirPit's 20 original spawn markers sit
                // at y 0.08..0.14 (docs/PLAN-AIRPIT.md item 6) so the same rule gives -4.4. A rule of thumb, to be
                // measured on the host (the dome must sit well above the ground, see ZeroLevelOverride).
                ZeroLevel = bay5 ? -8.2f : (equator || airPit) ? -4.4f : 0f,
                HeightFalloff = rig ? 0.025f : 0.029f,
                ReserveTextureBudget = rig,
                UseDonorCameraPrefab = rig,
                ReflectionIntensity = equator ? 0.85f : 1f,
                AmbientIntensity = equator ? 0.5f : 1f,
                IndoorFloorSmoothness = rig ? 0.5f : 1f,
                IndoorCoverPercent = equator ? 30 : 50,
                IndoorReflectionStrength = equator ? 0.95f : 1f,
                IndoorSurfaceRepair = bay5,
                ReflectiveSpecularCap = equator ? 0.3f : 1f,
            };
        }

        /// <summary>
        /// SunIntensityMultiplier / SunColorWhiteBlend scale the map's own authored directional "sun" light
        /// (Equator's FAKE_SUN). A map without one (Bay5: the donor sun is controlled in LightingLab; AirPit:
        /// no Directional light in any of its scenes) must not bind them, so no retired-key entry is needed
        /// later. The descriptor's SunPaths are the single source for "has an authored sun".
        /// </summary>
        internal static bool BindsAuthoredSunKeys(ArenaMapDescriptor map) => map.SunPaths.Length > 0;
    }
}
