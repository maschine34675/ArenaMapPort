using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// PLAN-TWO-MAPS item 5 follow-up (2026-09-25): no <c>SmallPhysicsSystem.Update</c> exceptions at raid end.
    ///
    /// Vanilla: <c>SmallPhysicsSystem</c> (D:/SPT41/Development/_decompiled/SmallPhysicsSystem.cs) is a plain scene
    /// object the first waking <c>SmallPhysicsObject</c> creates; its <c>Update()</c> reads
    /// <c>Singleton&lt;GameWorld&gt;.Instance.RegisteredPlayers</c> unconditionally and props never unregister.
    /// At raid end the GameWorld singleton is cleared a few frames before the raid scenes (and this object) are
    /// unloaded, so every one of those frames logs <c>NullReferenceException: SmallPhysicsSystem.Update()</c>
    /// (host raids 2026-09-25: 1–5 lines per raid end on both maps). Vanilla maps show the same order, but only
    /// our maps ship hundreds of props, so the noise is ours to silence: this prefix skips the tick while no
    /// GameWorld exists. Gated on the system object living in one of this mod's scenes; vanilla maps are untouched.
    /// </summary>
    internal static class PhysicsPropTeardownRepairs
    {
        internal static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(SmallPhysicsSystem), nameof(SmallPhysicsSystem.Update));
            if (!HarmonyPatches.Verify(target, typeof(SmallPhysicsSystem), nameof(SmallPhysicsSystem.Update)))
            {
                return;
            }
            var prefix = AccessTools.Method(typeof(PhysicsPropTeardownRepairs), nameof(UpdatePrefix));
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogDebug("[Harmony] patched SmallPhysicsSystem.Update (raid-end GameWorld guard)");
        }

        public static bool UpdatePrefix(SmallPhysicsSystem __instance)
        {
            if (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance != null)
            {
                return true; // normal raid frame — vanilla body runs
            }
            Scene scene = __instance.gameObject.scene;
            if (!scene.IsValid() || !ArenaMapRaid.IsOwnedScene(scene.name))
            {
                return true; // not one of our maps — leave vanilla behaviour (and its exception) alone
            }
            return false; // our raid is tearing down: nothing to simulate without a GameWorld
        }
    }
}
