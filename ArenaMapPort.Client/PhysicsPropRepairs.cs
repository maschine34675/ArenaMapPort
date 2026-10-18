using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// PLAN-TWO-MAPS item 5 (2026-09-25): physics props that react to shots and touches.
    ///
    /// Arena's Equator/Bay5 content scenes each carry exactly one root-level GameObject named
    /// "PHYSIC" (Equator: 391 direct-child props / 2192 objects total under it; Bay5: 18 props) --
    /// EFT's own <see cref="Rigidbody"/> + native <c>SmallPhysicsObject</c> class, both carried over
    /// by the Unity import (tools/extract/extract_physics_props.py, PhysicsPropImporter.cs).
    /// Equator's PHYSIC root ships <c>activeSelf=False</c> in Arena's own authored data with no
    /// toggler anywhere in the 0.16.9.5 decompile (almost certainly a networked-determinism
    /// precaution, irrelevant offline); each individual prop's OWN GameObject stays
    /// <c>activeSelf=True</c> (verified this spike, physics-spike/find_inactive_ancestors.py) --
    /// only the shared container is off. Bay5's PHYSIC root is already active in Arena's own data.
    /// User decision 2026-09-25 (docs/PLAN-TWO-MAPS.md §4.1): activate by default on both maps -- a
    /// scene-dump check this spike found no active static duplicate of any PHYSIC mesh within 0.3 m,
    /// so enabling adds clutter that is simply missing today, never double geometry.
    ///
    /// <c>SmallPhysicsObject.Awake()</c> (D:/SPT41/Development/_decompiled/SmallPhysicsObject.cs:
    /// 124-158) self-wires from BallisticCollider/Rigidbody children via
    /// <c>GetComponentsInChildren(includeInactive: true, ...)</c> and needs no unrecoverable field
    /// data -- Unity calls <c>Awake()</c> the first time a GameObject becomes active, so
    /// <c>SetActive(true)</c> on Equator's never-active PHYSIC root fires it for all 391 props in one
    /// call, exactly like ordinary scene load already does for Bay5's always-active container.
    /// <c>SmallPhysicsSystem.Instance</c> (D:/SPT41/Development/_decompiled/SmallPhysicsSystem.cs:
    /// 32-51) is a SELF-CREATING singleton -- unlike EnvironmentManager/ObservedCullingManager, its
    /// getter unconditionally does <c>if (_instance == null) { ... obj.AddComponent
    /// &lt;SmallPhysicsSystem&gt;() ... }</c>, so it needs no client-side fallback creation the way
    /// EnvironmentRepairs.cs creates EnvironmentManager: Awake()'s own
    /// <c>SmallPhysicsSystem.Instance.AddObject(this)</c> call creates it lazily the first time any
    /// prop wakes. <c>SmallPhysicsSystem.Update()</c> only simulates props within 5 m of a registered
    /// player under a measured-FPS budget (<c>_maxUpdateObjects</c>), so activating 391 props is not
    /// an unbounded per-frame cost.
    ///
    /// Runs from the same raid-start postfix chain as the other spawn-time repairs (idempotent:
    /// SetActive to an already-matching state is a harmless no-op, and <c>SpawnPointsCollection.
    /// CreateFromScene</c> running twice per raid re-applies the identical target state both times).
    /// </summary>
    internal static class PhysicsPropRepairs
    {
        const string Tag = "[RaidFix]";
        const string ContainerName = "PHYSIC";
        static ConfigEntry<bool> _enabled;

        internal static void BindConfig()
        {
            _enabled = Plugin.Instance.Settings.Bind("Physics", "EnablePhysicsProps", true,
                "Activate Arena's PHYSIC prop containers (Equator: 391 props, ships hierarchy-disabled in " +
                "Arena's own data; Bay5: 18, already active) so cups/bottles/boxes react to shots and touches " +
                "like a vanilla EFT map. Disable to turn the PHYSIC prop containers off on both maps. " +
                "Takes effect on the next raid.");
        }

        internal static void Apply()
        {
            if (_enabled == null || !ArenaMapRaid.IsLoaded())
            {
                return;
            }
            bool want = _enabled.Value;
            int toggled = 0, alreadyAtTarget = 0, smallPhysicsObjectTotal = 0, rigidbodyTotal = 0;
            var sb = new StringBuilder();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !ArenaMapRaid.IsOwnedScene(scene.name))
                {
                    continue;
                }
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != ContainerName)
                    {
                        continue;
                    }
                    if (root.activeSelf == want)
                    {
                        alreadyAtTarget++;
                    }
                    else
                    {
                        root.SetActive(want);
                        toggled++;
                    }
                    smallPhysicsObjectTotal += root.GetComponentsInChildren<global::SmallPhysicsObject>(true).Length;
                    rigidbodyTotal += root.GetComponentsInChildren<Rigidbody>(true).Length;
                    sb.Append($"\n    '{scene.name}/{root.name}': activeSelf={root.activeSelf}");
                }
            }
            if (toggled > 0 || alreadyAtTarget > 0)
            {
                Plugin.Log.LogDebug($"{Tag} physics props: {(want ? "activated" : "deactivated")} {toggled} PHYSIC " +
                                    $"container(s) ({alreadyAtTarget} already at target), {smallPhysicsObjectTotal} " +
                                    $"SmallPhysicsObject / {rigidbodyTotal} Rigidbody component(s) found under them{sb}");
            }
        }
    }
}
