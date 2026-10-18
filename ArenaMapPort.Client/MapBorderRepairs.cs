using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ArenaMapPort.Shared;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Playtest finding 2026-09-14 20:09 — "many large flat WHITE untextured blocks stand in walkways
    /// and block movement". Root cause (lead, verified with UnityPy directly against Arena's own level
    /// files — see disabled_components.json/count_disabled.py referenced by the finding): the extractor
    /// never reads m_Enabled for components and the importer never sets .enabled, so every component
    /// Arena itself ships DISABLED comes back ENABLED in our bundle.
    ///
    /// Item 1 — TeamFight play-area borders. Arena's own *_TeamFight_DesignStuff scene authors a batch
    /// of objects under a 'LEVEL-BORDERS TF' parent, layer 29 ("LevelBorder" in Arena's own TagManager —
    /// unnamed/blank at layer 29 in ours, see TagManager.asset), each carrying a BoxCollider + MeshFilter
    /// + MeshRenderer. In Arena these MeshRenderers are DISABLED (Equator: 49 counted; Bay5: 951 objects
    /// on the same layer per the multimap-generalize.md scout data) but the BoxColliders stay ENABLED —
    /// they are invisible walls that exist only to bound Arena's own TeamFight game mode; for a normal
    /// raid they read as solid geometry cutting the map in half. Our extractor/importer gap makes the
    /// renderers enabled too (the reported white blocks) on top of the pre-existing wall.
    ///
    /// Multimap generalization (docs/progress/multimap-generalize.md): Item 1's own mechanism is already
    /// map-agnostic — it walks every owned scene's hierarchy matching on LAYER or a NAME PREFIX, neither
    /// of which is Equator-specific — so generalizing it is only per-map STATE (its own
    /// "RemoveTeamFightBorders" config toggle and border list per map, config section
    /// "Map (&lt;key&gt;)") plus resolving the map from scene events the same ordering-hazard-free way
    /// every other repair file does (see ArenaMapRaid's own doc comment). No `TeamFightDesignStuffSceneName`
    /// descriptor field was added for this — Item 1 never gates on a specific scene name, only on the
    /// LevelBorder layer/name-prefix, confirmed by re-reading this file's own logic.
    ///
    /// Item 2 — narrow, name-based safety net for the bundle that is CURRENTLY deployed (built before any
    /// m_Enabled-aware importer fix), so the user gets relief before a rebuild/redeploy. It matches only
    /// three specific name shapes, all from Arena_Equator/Arena_Equator_Lighting (level51/55): any object
    /// named exactly 'Cube' in Arena_Equator_Lighting (19 objects, MeshRenderer+BoxCollider both disabled
    /// in Arena), any object whose name ends '_stencil'/'_STENCIL' with no further suffix (MeshRenderer
    /// only disabled in Arena — Unity's duplicate-name '&lt;name&gt; (N)' copies, e.g.
    /// 'mall_ceiling_01_stencil (1)', do NOT match this check), and any object named exactly
    /// 'colider &lt;N&gt;' (e.g. 'colider 46'/'colider 45'/'colider 2' — MeshRenderer+MeshCollider both
    /// disabled in Arena; Arena's own literal spelling, not a typo introduced here).
    ///
    /// It is NOT exhaustive (see the pre-generalization history for the exact counted gap), and — per
    /// docs/progress/multimap-generalize.md's explicit decision — it is deliberately NOT generalized to
    /// Bay5 or any future map: it is a one-time stopgap for the specific already-shipped, pre-fix Equator
    /// bundle. A freshly-built map goes through the m_Enabled-aware importer from the start and needs no
    /// name-based safety net (Bay5 in particular has ~10-40x Equator's disabled-component count with no
    /// exhaustive name pattern that would safely cover it — a disabled ceiling object literally named
    /// "Stensil", a typo, is one confirmed counter-example). If a freshly-built map ever appears to need
    /// this kind of fallback, that is a sign the m_Enabled round-trip fix itself regressed — investigate
    /// that, don't extend this name-based net.
    /// </summary>
    internal static class MapBorderRepairs
    {
        const string LevelBorderLayerName = "LevelBorder";
        const string LevelBorderNamePrefix = "LevelBorder";

        // Resolved once — LayerMask.NameToLayer reads project-static TagManager data that never changes
        // at runtime. -1 (unresolved, our project's own TagManager.asset leaves layer 29 unnamed) means
        // every owned scene falls back to the name-prefix check below instead.
        static readonly int LevelBorderLayer = LayerMask.NameToLayer(LevelBorderLayerName);

        // Arena's own literal (misspelled) authoring: "colider 46", "colider 45", "colider 2", etc.
        // Item 2 only — Equator-only, see class doc comment.
        static readonly Regex ColiderNamePattern = new Regex(@"^colider \d+$", RegexOptions.Compiled);

        // TeamFight border collider/renderer pairs found this raid — captured once per owned-scene load,
        // reapplied live on SettingChanged without rescanning the hierarchy. One MapState per registered
        // map (Item 1 only — Item 2's counters below stay global/Equator-only, see class doc comment).
        struct BorderEntry
        {
            internal Collider Collider;
            internal MeshRenderer Renderer;
        }

        sealed class MapState
        {
            internal ConfigEntry<bool> RemoveTeamFightBorders;
            internal readonly List<BorderEntry> Borders = new List<BorderEntry>();
            internal bool LoggedThisRaid;
        }

        static readonly Dictionary<string, MapState> _states = new Dictionary<string, MapState>();
        static bool _installed;

        // Item 2 counts only — those objects are forced off unconditionally (no config, nothing to
        // re-toggle live), so nothing but the count needs to survive past each scene's own scan.
        // Deliberately NOT per-map: Item 2 only ever fires while scanning Equator's own owned
        // scenes (Arena_Equator + Arena_Equator_Lighting — see ScanScene's isEquator gate), never
        // Bay5's or any future map's.
        static int _safetyNetCubeCount;
        static int _safetyNetStencilCount;
        static int _safetyNetColiderCount;

        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;

            foreach (var map in ArenaMapRegistry.All)
            {
                var state = new MapState();
                string configSection = $"Map ({map.Key})";
                // Default per map: a map whose descriptor keeps its LevelBorder cubes (Bowl: the stadium's only bound,
                // docs/PLAN-BOWL.md) starts with false; the key is new for such a map, so no saved value changes meaning.
                state.RemoveTeamFightBorders = Plugin.Instance.Settings.Bind(configSection, "RemoveTeamFightBorders", !map.KeepLevelBorders,
                    $"Removes the invisible TeamFight play-area walls so the whole {map.Key} map is walkable. " +
                    "false keeps Arena's TeamFight boundaries" +
                    (map.KeepLevelBorders ? " (the default here: this map's border cubes are its only play-area bound)." : "."));
                state.RemoveTeamFightBorders.SettingChanged += (_, __) => ReapplyIfLoaded(map, state);
                _states[map.Key] = state;
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ArenaMapRaid.IsOwnedScene(scene.name))
            {
                return; // every other map's raid — untouched
            }

            // Scene is already registered with SceneManager by the time sceneLoaded fires (see
            // ArenaMapRaid's own doc comment), so CurrentMap resolves even on the Scripts scene's own
            // load event.
            var map = ArenaMapRaid.CurrentMap;
            if (map == null || !_states.TryGetValue(map.Key, out var state))
            {
                return;
            }

            ScanScene(scene, state, map);
            ApplyBorders(state);
            MaybeLogSummary(map, state);
        }

        static void OnSceneUnloaded(Scene scene)
        {
            // Stateless resolution (not ArenaMapRaid.CurrentMap) — see ArenaMapRaid's own doc comment
            // for why an unload handler must never depend on a shared mutable "current map" field.
            var map = ArenaMapRaid.MapForScriptsScene(scene.name);
            if (map == null || !_states.TryGetValue(map.Key, out var state))
            {
                return; // reset once per raid, on the raid's own Scripts-scene teardown
            }

            state.Borders.Clear(); // next raid gets fresh Collider/MeshRenderer instances — old refs would be stale/destroyed
            state.LoggedThisRaid = false;

            if (map == ArenaMapRegistry.Equator)
            {
                // Item 2 is Equator-only (see class doc comment) — its counters only ever get
                // incremented while scanning Equator's own owned scenes.
                _safetyNetCubeCount = 0;
                _safetyNetStencilCount = 0;
                _safetyNetColiderCount = 0;
            }
        }

        static void ReapplyIfLoaded(ArenaMapDescriptor map, MapState state)
        {
            if (ArenaMapRaid.CurrentMap == map)
            {
                ApplyBorders(state);
            }
        }

        // Single walk of this scene's hierarchy covering both items — cheap (scene-load-time only, no
        // per-frame cost) and avoids a second GetComponentsInChildren pass over the same objects.
        static void ScanScene(Scene scene, MapState state, ArenaMapDescriptor map)
        {
            // Item 2 is Equator-only (see class doc comment) — gated on the resolved map, not a
            // per-check name/scene literal, so every one of its three sub-checks (Cube/_stencil/
            // 'colider N') is uniformly excluded from every other registered map, including Bay5.
            // Fixed 2026-09-15: only the Cube sub-check used to carry this gate (as
            // `isLightingScene`) — the _stencil/'colider N' checks ran unconditionally for every
            // owned scene of every map, including Bay5's imported scenes, which carry real objects
            // matching both patterns (e.g. 'Bay5_Warehouse_STENCIL' in the imported Light/Main
            // scenes) — contradicting this class's own doc comment ("Item 2 only ever fires while
            // scanning Equator's own Lighting scene", true only for Cube).
            bool isEquator = map == ArenaMapRegistry.Equator;
            // Cube additionally stays scene-scoped within Equator — Arena's own 19 'Cube' objects
            // live only in Arena_Equator_Lighting (docs/progress/fix-enabled-state.md), never in
            // Arena_Equator itself, so this narrower check is intentional, not a second copy of the
            // map gate. _stencil/'colider N' are NOT scene-scoped: Arena's own authoring puts both
            // patterns in Arena_Equator (main) too (e.g. 'PKTS-6281_Admiral_opened_STENCIL',
            // 'colider 46'/'45'/'2' — same doc), so narrowing them to the Lighting scene the way
            // Cube is scoped would silently stop covering Arena_Equator's own matches.
            bool isLightingScene = isEquator && scene.name == ArenaEquatorLocation.LightingSceneName;

            foreach (var root in scene.GetRootGameObjects())
            {
                var transforms = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    var go = transforms[i].gameObject;
                    string name = go.name;

                    if (IsLevelBorderObject(go))
                    {
                        var collider = go.GetComponent<Collider>();
                        var renderer = go.GetComponent<MeshRenderer>();
                        if (collider != null || renderer != null)
                        {
                            state.Borders.Add(new BorderEntry { Collider = collider, Renderer = renderer });
                        }
                    }

                    // Item 2 in its entirety (Cube/_stencil/'colider N') is Equator-only (see class
                    // doc comment and the isEquator/isLightingScene setup above). Fixed 2026-09-15:
                    // the _stencil/'colider N' checks below used to run unconditionally for every
                    // owned scene of every registered map (including Bay5's own imported scenes,
                    // which carry real objects matching both patterns, e.g.
                    // 'Bay5_Warehouse_STENCIL' in the imported Light/Main scenes) -- only the Cube
                    // check was ever actually gated, contradicting this class's own doc comment.
                    if (isLightingScene && name == "Cube")
                    {
                        var collider = go.GetComponent<Collider>();
                        var renderer = go.GetComponent<MeshRenderer>();
                        if (collider != null)
                        {
                            collider.enabled = false;
                        }
                        if (renderer != null)
                        {
                            renderer.enabled = false;
                        }
                        if (collider != null || renderer != null)
                        {
                            _safetyNetCubeCount++;
                        }
                    }

                    if (isEquator &&
                        (name.EndsWith("_stencil", StringComparison.Ordinal) ||
                         name.EndsWith("_STENCIL", StringComparison.Ordinal)))
                    {
                        var renderer = go.GetComponent<MeshRenderer>();
                        if (renderer != null)
                        {
                            renderer.enabled = false;
                            _safetyNetStencilCount++;
                        }
                    }

                    if (isEquator && ColiderNamePattern.IsMatch(name))
                    {
                        var renderer = go.GetComponent<MeshRenderer>();
                        var meshCollider = go.GetComponent<MeshCollider>();
                        if (renderer != null)
                        {
                            renderer.enabled = false;
                        }
                        if (meshCollider != null)
                        {
                            meshCollider.enabled = false;
                        }
                        if (renderer != null || meshCollider != null)
                        {
                            _safetyNetColiderCount++;
                        }
                    }
                }
            }
        }

        // Primary: Arena's own "LevelBorder" layer, when our project names layer 29 the same way (it
        // currently does not — TagManager.asset leaves it blank, so LevelBorderLayer is -1 and every
        // owned scene takes the fallback path). Fallback: the 'LevelBorder (N)' name Arena itself authors,
        // which survives regardless of which numeric layer slot the importer happens to assign.
        static bool IsLevelBorderObject(GameObject go)
        {
            if (LevelBorderLayer >= 0)
            {
                return go.layer == LevelBorderLayer;
            }
            return go.name.StartsWith(LevelBorderNamePrefix, StringComparison.Ordinal);
        }

        static void ApplyBorders(MapState state)
        {
            bool remove = state.RemoveTeamFightBorders.Value;
            for (int i = 0; i < state.Borders.Count; i++)
            {
                var entry = state.Borders[i];
                if (entry.Collider != null)
                {
                    entry.Collider.enabled = !remove;
                }
                if (entry.Renderer != null)
                {
                    // Arena parity: these never render regardless of the setting above — the walls
                    // themselves are (by design) invisible in Arena even while solid for TeamFight.
                    entry.Renderer.enabled = false;
                }
            }
        }

        // Fires exactly once per raid: every OnSceneLoaded call for one of this map's owned scenes
        // re-checks whether all of them are now loaded, and only the one call where that first becomes
        // true logs — so the single summary line is correct regardless of the actual per-scene load order.
        static void MaybeLogSummary(ArenaMapDescriptor map, MapState state)
        {
            if (state.LoggedThisRaid || !AllOwnedScenesLoaded(map))
            {
                return;
            }
            state.LoggedThisRaid = true;

            int safetyNetTotal = _safetyNetCubeCount + _safetyNetStencilCount + _safetyNetColiderCount;
            Plugin.Log.LogDebug(
                $"[ArenaMapPort][RaidFix] {map.Key} TeamFight borders: {state.Borders.Count} object(s) matched " +
                $"(layer '{LevelBorderLayerName}'={LevelBorderLayer}, name-prefix fallback " +
                $"{(LevelBorderLayer < 0 ? "active" : "unused")}) — RemoveTeamFightBorders=" +
                $"{state.RemoveTeamFightBorders.Value} (colliders " +
                $"{(state.RemoveTeamFightBorders.Value ? "disabled" : "left enabled")}, renderers always " +
                $"disabled)." +
                (map == ArenaMapRegistry.Equator
                    ? $" Legacy safety net for the currently deployed bundle (no-op once rebuilt): " +
                      $"{safetyNetTotal} object(s) forced off (Cube={_safetyNetCubeCount}, " +
                      $"*_stencil={_safetyNetStencilCount}, 'colider N'={_safetyNetColiderCount})."
                    : string.Empty));
        }

        static bool AllOwnedScenesLoaded(ArenaMapDescriptor map)
        {
            var names = map.AllSceneNames;
            for (int i = 0; i < names.Length; i++)
            {
                var scene = SceneManager.GetSceneByName(names[i]);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
