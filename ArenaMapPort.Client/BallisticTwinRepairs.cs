using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// Stage 1 (docs/HANDOFF.md, 2026-09-20; deferred item of docs/progress/fix-combat.md): bullets ignore
    /// LowPolyCollider-only geometry.
    ///
    /// EFT's ballistics raycast (<c>BallisticsCalculatorConstants.HitMask</c>) does not include the LowPolyCollider
    /// layer — that layer is the player-movement collision, bullets are meant to hit the HighPolyCollider twin
    /// Arena authors next to it ("X_COLLIDER" on LowPolyCollider + "X_BALLISTIC_&lt;material&gt;" on HighPolyCollider,
    /// e.g. the roller shutter in BoothDoorRepairs). On Equator 1,705 of the active LowPolyCollider colliders have
    /// such a twin; 13 do not (work/extract/equator/scenes/Arena_Equator.json: rack_module12 ×4,
    /// heap_conc_frag_6 ×2, the RESPAWN_1 "doors_spining" leaves ×2, reserve_fence_ema_nocurt2 ×2, metall_fence_2,
    /// fence_ema_nocurt2 ×2) — bullets pass through those props without a hit. This adds a HighPolyCollider twin
    /// (a child object carrying a copy of the collider) to every LowPolyCollider collider whose parent subtree
    /// has no HighPolyCollider collider, once per raid, before the raid starts. A bare collider on that layer is
    /// what every other ballistic collider of the ported scene is as well (the import drops BSG's
    /// BallisticCollider MonoBehaviour), so the twin behaves like its 2,175 siblings. Never adds a bare
    /// <c>BaseBallistic</c> (fix-combat.md: its Get() returns null and Shot.HandleCollision dereferences it).
    /// Map-agnostic: runs on every owned scene.
    /// </summary>
    internal static class BallisticTwinRepairs
    {
        const string Tag = "[RaidFix]";
        const string TwinSuffix = "_BALLISTIC_twin";
        static ConfigEntry<bool> _enabled;

        internal static void BindConfig()
        {
            _enabled = Plugin.Instance.Settings.Bind("Combat", "BallisticTwins", true,
                "Give every LowPolyCollider-only collider a HighPolyCollider twin at raid start so bullets hit it " +
                "(EFT's ballistics mask skips the LowPolyCollider layer; Arena authors a ballistic twin for almost " +
                "every prop, a few lack one). Takes effect on the next raid.");
        }

        internal static void Apply()
        {
            if (_enabled == null || !_enabled.Value || !ArenaMapRaid.IsLoaded())
            {
                return;
            }
            int low = LayerMask.NameToLayer("LowPolyCollider");
            int high = LayerMask.NameToLayer("HighPolyCollider");
            if (low < 0 || high < 0)
            {
                Plugin.Log.LogWarning($"{Tag} ballistic twins: layers not found (LowPolyCollider={low}, HighPolyCollider={high})");
                return;
            }

            int added = 0, alreadyTwinned = 0, unsupported = 0;
            var names = new StringBuilder();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !ArenaMapRaid.IsOwnedScene(scene.name))
                {
                    continue;
                }
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var collider in root.GetComponentsInChildren<Collider>(false))
                    {
                        if (collider.gameObject.layer != low || !collider.enabled || collider.isTrigger)
                        {
                            continue;
                        }
                        if (collider.name.EndsWith(TwinSuffix))
                        {
                            continue;
                        }
                        if (collider.name.StartsWith(AuthoredBorderRepairs.ObjectPrefix, System.StringComparison.Ordinal))
                        {
                            continue; // the descriptor's invisible play-area walls are movement-only; bullets pass them
                        }
                        // the twin lives under the same parent in Arena's authoring; a previous pass of this
                        // method (EFT runs the spawn postfix twice per raid) counts as one too
                        var scope = collider.transform.parent != null ? collider.transform.parent : collider.transform;
                        if (HasColliderOnLayer(scope, high))
                        {
                            alreadyTwinned++;
                            continue;
                        }
                        if (!TryClone(collider, high))
                        {
                            unsupported++;
                            continue;
                        }
                        added++;
                        if (added <= 40)
                        {
                            names.Append($"\n    {Path(collider.transform)} ({collider.GetType().Name}) at {collider.bounds.center:F1}");
                        }
                    }
                }
            }
            if (added > 0 || unsupported > 0)
            {
                Plugin.Log.LogDebug($"{Tag} ballistic twins: added {added} HighPolyCollider twin(s) for LowPolyCollider-only " +
                                   $"colliders ({alreadyTwinned} already had one, {unsupported} unsupported collider type(s)){names}");
            }
        }

        static bool HasColliderOnLayer(Transform scope, int layer)
        {
            foreach (var c in scope.GetComponentsInChildren<Collider>(false))
            {
                if (c.gameObject.layer == layer && c.enabled && !c.isTrigger)
                {
                    return true;
                }
            }
            return false;
        }

        static bool TryClone(Collider source, int layer)
        {
            var go = new GameObject(source.name + TwinSuffix);
            go.layer = layer;
            go.transform.SetParent(source.transform, false); // same world transform as the source collider
            switch (source)
            {
                case MeshCollider mesh:
                    var m = go.AddComponent<MeshCollider>();
                    m.sharedMesh = mesh.sharedMesh;
                    m.convex = mesh.convex;
                    m.cookingOptions = mesh.cookingOptions;
                    return true;
                case BoxCollider box:
                    var b = go.AddComponent<BoxCollider>();
                    b.center = box.center;
                    b.size = box.size;
                    return true;
                case SphereCollider sphere:
                    var s = go.AddComponent<SphereCollider>();
                    s.center = sphere.center;
                    s.radius = sphere.radius;
                    return true;
                case CapsuleCollider capsule:
                    var c = go.AddComponent<CapsuleCollider>();
                    c.center = capsule.center;
                    c.radius = capsule.radius;
                    c.height = capsule.height;
                    c.direction = capsule.direction;
                    return true;
                default:
                    Object.Destroy(go);
                    return false;
            }
        }

        static string Path(Transform t)
        {
            var parts = new List<string>();
            for (var cur = t; cur != null; cur = cur.parent)
            {
                parts.Add(cur.name);
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
