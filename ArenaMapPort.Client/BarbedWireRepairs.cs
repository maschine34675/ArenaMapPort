using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using EFT.Interactive;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// PLAN-TWO-MAPS item 2 (2026-09-25): barbed wire that hurts.
    ///
    /// Arena's concertina-wire props ("bruno_helix", 246 on Bay5: harbour fences, container hallways, the
    /// centre) carry EFT's own <see cref="BarbedWire"/> (a <see cref="DamageTrigger"/>: contact damage through
    /// <c>BodyPartCollider.ProceedBarb</c>, sprint off, a speed limit while inside) on a trigger BoxCollider.
    /// The import keeps the trigger colliders (192 active + 32 inactive in the Bay5 package) but drops every
    /// MonoBehaviour, so the wire was decoration. This attaches <see cref="ArenaBarbedWire"/> — the vanilla
    /// component with one difference: its <c>_soundBank</c> is an unrecoverable per-instance field and there
    /// is no barbed-wire bank in the SPT streaming assets, so <c>PlaySound</c> is skipped while the bank is
    /// null instead of throwing inside BetterAudio. Map-agnostic by name prefix (the same prop family exists on
    /// other Arena maps); runs from the spawn postfix like the other raid-start repairs and is idempotent.
    ///
    /// PLAN-AIRPIT item 15 (2026-10-12): there is no map gate to widen. The only gates are
    /// <c>ArenaMapRaid.IsLoaded()</c> and <c>IsOwnedScene</c>, both over <c>ArenaMapRegistry.All</c>, so AirPit
    /// is covered since it joined the released set. AirPit (Arena 0.6.0 base scene) carries 26 BarbedWire
    /// components: 12 on active <c>bruno_helix</c> objects with an enabled trigger BoxCollider (the ones this
    /// attaches, all in <c>Arena_AirPit_ShootOut</c>) and 14 on inactive <c>Bruno_LOD0</c> LOD mesh children whose
    /// only collider is a solid MeshCollider (no trigger box, so nothing to attach to). Equator has none. Equator
    /// and Bay5 behave exactly as before. Host test: docs/progress/airpit-release.md, item 15.
    /// </summary>
    internal static class BarbedWireRepairs
    {
        const string Tag = "[ArenaMapPort][RaidFix]";
        const string NamePrefix = "bruno_helix";
        static ConfigEntry<bool> _enabled;

        internal static void BindConfig()
        {
            _enabled = Plugin.Instance.Settings.Bind("Combat", "BarbedWire", true,
                "Give Arena's concertina wire (bruno_helix props) EFT's barbed-wire behaviour: contact damage, sprint " +
                "off and a speed limit while inside, like vanilla maps. Takes effect on the next raid.");
        }

        internal static void Apply()
        {
            if (_enabled == null || !_enabled.Value || !ArenaMapRaid.IsLoaded())
            {
                return;
            }
            int added = 0, present = 0;
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
                    foreach (var box in root.GetComponentsInChildren<BoxCollider>(false))
                    {
                        if (!box.isTrigger || !box.name.StartsWith(NamePrefix))
                        {
                            continue;
                        }
                        if (box.GetComponent<DamageTrigger>() != null)
                        {
                            present++;
                            continue;
                        }
                        box.gameObject.AddComponent<ArenaBarbedWire>();
                        added++;
                        if (added <= 5)
                        {
                            sb.Append($"\n    '{box.name}' at {box.bounds.center:F1} size {box.bounds.size:F1}");
                        }
                    }
                }
            }
            if (added > 0 || present > 0)
            {
                Plugin.Log.LogDebug($"{Tag} barbed wire: attached {added} BarbedWire trigger(s) ({present} already present){sb}");
            }
        }
    }

    /// <summary>Vanilla <see cref="BarbedWire"/> whose sound is optional (no per-instance SoundBank survives the import).</summary>
    public sealed class ArenaBarbedWire : BarbedWire
    {
        public override void PlaySound(bool useOcclusion = false)
        {
            if (_soundBank != null)
            {
                base.PlaySound(useOcclusion);
            }
        }
    }
}
