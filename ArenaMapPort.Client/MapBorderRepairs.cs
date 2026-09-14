using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
    /// Item 1 — TeamFight play-area borders. Arena's own Arena_Equator_TeamFight_DesignStuff (level57)
    /// scene authors 41 objects under a 'LEVEL-BORDERS TF' parent, layer 29 ("LevelBorder" in Arena's own
    /// TagManager — unnamed/blank at layer 29 in ours, see TagManager.asset), each carrying a BoxCollider
    /// + MeshFilter + MeshRenderer. In Arena these MeshRenderers are DISABLED (49 counted) but the
    /// BoxColliders stay ENABLED — they are invisible walls that exist only to bound Arena's own
    /// TeamFight game mode; for a normal (non-TeamFight) Equator raid they read as solid geometry cutting
    /// the map in half. Our extractor/importer gap makes the renderers enabled too (the reported white
    /// blocks) on top of the pre-existing wall. Gated on <see cref="ArenaEquatorRaid.IsOwnedScene"/> /
    /// <see cref="ArenaEquatorRaid.IsLoaded"/> throughout — every other map's raid is untouched.
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
    /// It is NOT exhaustive — cross-checked against the playtest's root-cause dump
    /// (work/extract/equator/scenes/*.json), these three patterns miss roughly 400 other
    /// disabled-in-Arena components with unrelated names: essentially everything in
    /// Arena_Equator_DesignStuff (level53 — 388 objects: mannequin-gear BoxColliders like 'Loot',
    /// 'CR_ctacticall_body_lod0', 'AR_6B2_lod0/1'; 'Graffiti_Art_21' MeshRenderers; 'mustache_LOD1'
    /// MeshCollider — none of them named 'Cube', '*_stencil' or 'colider &lt;N&gt;'), the ~21
    /// 'ballistic'-named MeshColliders disabled in Arena_Equator alongside the 3 'colider &lt;N&gt;'
    /// ones this check does catch, the 3 duplicate-suffixed stencil renderers noted above in
    /// Arena_Equator_Lighting, and the 8 'AreaLight'/'AreaLight_GreenExit_small (1)' MeshRenderers
    /// disabled in Arena_Equator_TeamFight_DesignStuff alongside the 41 'LevelBorder (N)' ones Item 1
    /// already handles. Deliberately narrow and name-based rather than a general m_Enabled fix (that
    /// belongs in the extractor/importer, out of scope for this plugin) — every match here becomes a
    /// no-op once the bundle is rebuilt from an importer that honors m_Enabled, since the objects will
    /// already come in disabled; the ~400 objects it never matched become correct too, but via the
    /// rebuilt bundle's own enabled=false, not via anything this safety net does.
    /// </summary>
    internal static class MapBorderRepairs
    {
        const string ConfigSection = "Map (arena_equator)";
        const string LevelBorderLayerName = "LevelBorder";
        const string LevelBorderNamePrefix = "LevelBorder";

        // Resolved once — LayerMask.NameToLayer reads project-static TagManager data that never changes
        // at runtime. -1 (unresolved, our project's own TagManager.asset leaves layer 29 unnamed) means
        // every owned scene falls back to the name-prefix check below instead.
        static readonly int LevelBorderLayer = LayerMask.NameToLayer(LevelBorderLayerName);

        // Arena's own literal (misspelled) authoring: "colider 46", "colider 45", "colider 2", etc.
        static readonly Regex ColiderNamePattern = new Regex(@"^colider \d+$", RegexOptions.Compiled);

        static ConfigEntry<bool> _removeTeamFightBorders;
        static bool _installed;
        static bool _loggedThisRaid;

        // TeamFight border collider/renderer pairs found this raid — captured once per owned-scene load,
        // reapplied live on SettingChanged without rescanning the hierarchy.
        struct BorderEntry
        {
            internal Collider Collider;
            internal MeshRenderer Renderer;
        }
        static readonly List<BorderEntry> _borders = new List<BorderEntry>();

        // Item 2 counts only — those objects are forced off unconditionally (no config, nothing to
        // re-toggle live), so nothing but the count needs to survive past each scene's own scan.
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

            _removeTeamFightBorders = Plugin.Instance.Config.Bind(ConfigSection, "RemoveTeamFightBorders", true,
                "Removes the invisible TeamFight play-area walls so the whole Equator map is walkable. " +
                "false keeps Arena's TeamFight boundaries.");
            _removeTeamFightBorders.SettingChanged += (_, __) => ReapplyIfLoaded();

            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!ArenaEquatorRaid.IsOwnedScene(scene.name))
            {
                return; // every other map's raid — untouched
            }

            ScanScene(scene);
            ApplyBorders();
            MaybeLogSummary();
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (scene.name != ArenaMapPort.Shared.ArenaEquatorLocation.ScriptsSceneName)
            {
                return; // reset once per raid, on the raid's own Scripts-scene teardown
            }

            _borders.Clear(); // next raid gets fresh Collider/MeshRenderer instances — old refs would be stale/destroyed
            _safetyNetCubeCount = 0;
            _safetyNetStencilCount = 0;
            _safetyNetColiderCount = 0;
            _loggedThisRaid = false;
        }

        static void ReapplyIfLoaded()
        {
            if (ArenaEquatorRaid.IsLoaded())
            {
                ApplyBorders();
            }
        }

        // Single walk of this scene's hierarchy covering both items — cheap (scene-load-time only, no
        // per-frame cost) and avoids a second GetComponentsInChildren pass over the same objects.
        static void ScanScene(Scene scene)
        {
            bool isLightingScene = scene.name == ArenaMapPort.Shared.ArenaEquatorLocation.LightingSceneName;

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
                            _borders.Add(new BorderEntry { Collider = collider, Renderer = renderer });
                        }
                    }

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

                    if (name.EndsWith("_stencil", StringComparison.Ordinal) ||
                        name.EndsWith("_STENCIL", StringComparison.Ordinal))
                    {
                        var renderer = go.GetComponent<MeshRenderer>();
                        if (renderer != null)
                        {
                            renderer.enabled = false;
                            _safetyNetStencilCount++;
                        }
                    }

                    if (ColiderNamePattern.IsMatch(name))
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

        static void ApplyBorders()
        {
            bool remove = _removeTeamFightBorders.Value;
            for (int i = 0; i < _borders.Count; i++)
            {
                var entry = _borders[i];
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

        // Fires exactly once per raid: every OnSceneLoaded call for one of our 6 owned scenes re-checks
        // whether all 6 are now loaded, and only the one call where that first becomes true logs — so the
        // single summary line is correct regardless of the actual per-scene load order.
        static void MaybeLogSummary()
        {
            if (_loggedThisRaid || !AllOwnedScenesLoaded())
            {
                return;
            }
            _loggedThisRaid = true;

            int safetyNetTotal = _safetyNetCubeCount + _safetyNetStencilCount + _safetyNetColiderCount;
            Plugin.Log.LogInfo(
                $"[ArenaMapPort][RaidFix] TeamFight borders: {_borders.Count} object(s) matched " +
                $"(layer '{LevelBorderLayerName}'={LevelBorderLayer}, name-prefix fallback " +
                $"{(LevelBorderLayer < 0 ? "active" : "unused")}) — RemoveTeamFightBorders=" +
                $"{_removeTeamFightBorders.Value} (colliders " +
                $"{(_removeTeamFightBorders.Value ? "disabled" : "left enabled")}, renderers always " +
                $"disabled). Legacy safety net for the currently deployed bundle (no-op once rebuilt): " +
                $"{safetyNetTotal} object(s) forced off (Cube={_safetyNetCubeCount}, " +
                $"*_stencil={_safetyNetStencilCount}, 'colider N'={_safetyNetColiderCount}).");
        }

        static bool AllOwnedScenesLoaded()
        {
            var names = ArenaMapPort.Shared.ArenaEquatorLocation.AllSceneNames;
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
