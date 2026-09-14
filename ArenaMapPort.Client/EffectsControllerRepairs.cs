using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// impl-raidstart-repairs item (3). Our scene's <c>LevelSettings.CameraPrefab</c> is null (SPEC §D —
    /// no camera-fidelity milestone for the spike), so <c>CameraManager.SetCameraFromSettings</c>
    /// (<c>EFT.CameraControl/CameraManager.cs:322-338</c>) falls back to BSG's own built-in "Cam2"
    /// resource — already graceful, no fix needed there. But Cam2 predates <c>FrostbiteEffect</c> (the
    /// arctic-cold screen effect): <c>EffectsController.cs:1364-1365</c>, inside <c>Init()</c> (called
    /// from <c>Awake()</c> at line 1219), is the ONLY <c>GetComponent</c> in that ~90-line routine with
    /// no <c>?? AddComponentCopy(...)</c> fallback like every sibling effect has —
    /// <c>_frostbiteEffect = base.gameObject.GetComponent&lt;FrostbiteEffect&gt;(); _frostbiteEffect
    /// .enabled = false;</c> NREs unconditionally on Cam2.
    ///
    /// This does NOT abort the raid (a Unity <c>Awake()</c> exception is logged and swallowed by the
    /// engine, not propagated through the async raid-init Task chain like the SpatialAudioSystem/
    /// EnvironmentManager crashes) but it does throw partway through <c>Init()</c>, before the
    /// subscriptions at <c>EffectsController.cs:1221-1222</c> run — so the whole first-person screen
    /// effects system (blood, vignette, DOF, chromatic aberration, damage/hydration/pain accumulators,
    /// thermal vision wiring) stays unwired for the entire raid unless this is fixed. Independently
    /// corroborated for the same EFT/SPT build (0.16.9.5) by ManimalIcebreaker's own analysis
    /// (<c>RaidFixPatches.cs:341-389</c>, "Cam2 predates FrostbiteEffect") — same root cause, same fix.
    ///
    /// Fix (matches Icebreaker's <c>Patch_EffectsControllerFrostbite</c>'s FrostbiteEffect half,
    /// <c>RaidFixPatches.cs:378-389</c>, MIT — `danauraborealis`; attribution in
    /// THIRD-PARTY-NOTICES.md; TOD_Camera/PostProcessResources healing from the rest of that patch is
    /// NOT ported — gated behind their own weather/TOD system, out of scope here per SPEC): add a bare
    /// <c>FrostbiteEffect</c> component before <c>EffectsController.Awake</c>'s body runs. Verified safe
    /// to add unconditionally — <c>FrostbiteEffect.Awake()</c>
    /// (<c>D:/SPT41/Development/_decompiled/FrostbiteEffect.cs:94-97</c>) only does
    /// <c>GetComponent&lt;SSAAPropagator&gt;()</c>, no other siblings required, and
    /// <c>EffectsController.Init()</c> immediately disables it again on the very next line once it
    /// stops NREing.
    ///
    /// Gated on <see cref="ArenaEquatorRaid.IsLoaded()"/> — valid here: <c>EffectsController.Awake</c>
    /// fires during local-player camera setup (<c>CameraManager.SetCameraFromPrefab</c>, called from
    /// <c>PlayerCameraController.Create</c>), which only happens after our scenes are loaded and
    /// <c>Player.Init</c> has run.
    /// </summary>
    internal static class EffectsControllerFrostbitePrefix
    {
        // Target: EffectsController.Awake(), public void — EffectsController.cs:1217-1227.
        // EffectsController is declared in the global namespace in this build (no `using` needed).
        internal static void Prefix(EffectsController __instance)
        {
            if (!ArenaEquatorRaid.IsLoaded())
            {
                return; // every other map's raid — untouched (retail camera prefabs ship the component)
            }

            if (__instance.GetComponent<FrostbiteEffect>() == null)
            {
                __instance.gameObject.AddComponent<FrostbiteEffect>();
                Plugin.Log.LogInfo("[ArenaMapPort][RaidFix] added missing FrostbiteEffect to fallback camera " +
                                    "(Cam2 predates it; EffectsController.Init disables it again immediately)");
            }
        }
    }
}
