// ArenaMapPort.Shared — linked into BOTH ArenaMapPort.Client and ArenaMapPort.Server (ArenaMode's own
// convention, ArenaMode.Client.csproj:19-26 <Compile Include="..\...Shared\...">). Identity constants only
// — see docs/SPEC.md §0 ("Identity constants (WP5 owns; never change after first deploy)").
//
// NOTE (wp4, 2026-09-13): WP5 had not run yet when this WP4 pass started, so this file did not exist and
// ArenaMapPort.Client had nothing to link against. Created here with exactly §0's values and nothing else
// (no behaviour, no WP5-specific fields) so the client compiles; WP5 owns this file going forward and may
// extend it, but must not change any existing value here without updating every consumer (§0's own warning
// — profiles/insurance would desync if IdField regenerates).
namespace ArenaMapPort.Shared
{
    public static class ArenaEquatorLocation
    {
        /// <summary>LocationTable dictionary key / base.json Id — lowercase, folder-safe.</summary>
        public const string Key = "arena_equator";

        /// <summary>base.json IdField — MongoId shape, 24 lowercase hex chars. Never regenerate (see §0).</summary>
        public const string IdField = "e9b8e12c60dfd2820e949462";

        /// <summary>base.json Scene.path — the preset bundle's served key (WP4's BundlesManager hook).</summary>
        public const string PresetBundleKey = "maps/arena_equator_preset.bundle";

        /// <summary>base.json Scene.rcid — the ScenesPreset MonoBehaviour's asset name inside the preset bundle.</summary>
        public const string PresetAssetName = "arena_equator.scenespreset.asset";

        /// <summary>Served key for the one bundle holding all 6 scenes (WP2 build output).</summary>
        public const string ScenesBundleKey = "arena_equator/arena_equator_scenes.bundle";

        /// <summary>Served key for the one-NavMeshData bundle (WP2 build output, fresh agent-0 bake).</summary>
        public const string NavMeshBundleKey = "arena_equator/arena_equator_navmesh.bundle";

        /// <summary>Client plugin assembly name (matches sibling ArenaMode.Client.csproj:7 naming).</summary>
        public const string ClientAssemblyName = "maschine-ArenaMapPort.Client";

        /// <summary>Server mod assembly name (matches sibling ArenaMode.Server.csproj:11 naming).</summary>
        public const string ServerAssemblyName = "maschine-ArenaMapPort.Server";

        /// <summary>SPT server mod folder name under user/mods/.</summary>
        public const string ServerModFolderName = "ArenaMapPort";

        /// <summary>BepInEx plugin folder name under BepInEx/plugins/.</summary>
        public const string ClientPluginFolderName = "maschine-ArenaMapPort";

        // --- Scene set (§E.1) --------------------------------------------------------------------
        // rcid values patched into the preset by WP3 (§B.WP3 step 3) MUST equal these exactly — a
        // one-character mismatch here is the single highest-leverage typo risk in the whole pipeline
        // (§B.WP3 edge cases). WP2's SceneAssembler must save each imported scene under this exact name.

        /// <summary>First-loaded scene (LoadSceneMode.Single) — the 5 fresh-authored EFT components (§D).</summary>
        public const string ScriptsSceneName = "Arena_Equator_Scripts";

        public const string MainSceneName = "Arena_Equator";
        public const string LightingSceneName = "Arena_Equator_Lighting";
        public const string DesignStuffSceneName = "Arena_Equator_DesignStuff";
        public const string TeamFightSceneName = "Arena_Equator_TeamFight";
        public const string TeamFightDesignStuffSceneName = "Arena_Equator_TeamFight_DesignStuff";

        /// <summary>All 6 scenes, in the exact load order §B.WP3 step 3's table specifies.</summary>
        public static readonly string[] AllSceneNames =
        {
            ScriptsSceneName,
            MainSceneName,
            LightingSceneName,
            DesignStuffSceneName,
            TeamFightSceneName,
            TeamFightDesignStuffSceneName,
        };
    }
}
