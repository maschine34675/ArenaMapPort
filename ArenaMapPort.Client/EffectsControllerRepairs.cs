using System;
using System.Reflection;
using HarmonyLib;
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

    /// <summary>
    /// fix-combat, findings F1 + F2 (explosives-items). <c>EffectsController.Init()</c>
    /// (<c>EffectsController.cs:1273-1381</c>) fetches five effect components straight off
    /// <c>base.gameObject</c> with a bare <c>GetComponent&lt;T&gt;()</c> and — unlike every sibling in that
    /// method (DOF/CC_FastVignette/CC_DoubleVision/etc, all <c>?? AddComponentCopy(_effectsPrefab...)</c>)
    /// — NO fallback: <c>_bloodOnScreen</c> (:1285), <c>_blur</c> (:1288, <c>FastBlur</c>), <c>_grenadeFlash</c>
    /// (:1326, <c>GrenadeFlashScreenEffect</c>), <c>_eyeBurn</c> (:1327, <c>EyeBurn</c>). Cam2 (our fallback
    /// camera — <c>LevelSettings.CameraPrefab</c> is null, SPEC §D) never carried these; on real maps they
    /// ship on the per-map camera prefab. Unlike <see cref="EffectsControllerFrostbitePrefix"/>'s
    /// <c>FrostbiteEffect</c>, a bare <c>AddComponent</c> is NOT safe for any of these four: <c>EyeBurn.Burn()</c>
    /// indexes an empty <c>[SerializeField]</c> material array (<c>EyeBurn.cs:204</c>);
    /// <c>GrenadeFlashScreenEffect.Awake()</c> dereferences its own unassigned <c>PrismEffects</c> field
    /// immediately (<c>GrenadeFlashScreenEffect.cs:30</c>); <c>BloodOnScreen.Start()</c> passes its unassigned
    /// <c>BloodShader</c> field straight into <c>new Material(Shader)</c> (<c>BloodOnScreen.cs:237</c>, throws
    /// on a null shader); <c>FastBlur.Update()</c> evaluates its unassigned <c>_hitCurve</c>/<c>_deathCurve</c>
    /// AnimationCurve every frame once enabled (<c>FastBlur.cs:105-115</c>) — all four would trade one NRE for
    /// another (or a Material-constructor exception) the instant they were added. So the fix here is
    /// guard-and-skip (documented "no screen effect" degrade), not add-component.
    ///
    /// <c>_eyeBurn</c>/<c>_bloodOnScreen</c>/<c>_blur</c> are <c>private</c> fields on <c>EffectsController</c>
    /// (<c>_grenadeFlash</c> too, but <see cref="EffectsController.FlashAccumulator"/>'s own copy,
    /// <c>_screenEffect</c>, is <c>public</c> — see <see cref="FlashAccumulatorGuardPrefix"/>) — read via
    /// <c>AccessTools.Field</c> rather than Harmony's <c>___fieldName</c> injection convention, matching this
    /// project's stated preference (<see cref="HarmonyPatches"/>'s header comment) for explicit, verifiable
    /// reflection over attribute magic. Each guard treats a failed field lookup (a future build renaming the
    /// field) the same way <see cref="HarmonyPatches.Verify"/> treats a missing method target: fall back to
    /// vanilla behaviour (<c>return true</c> / rethrow) rather than risk NREing inside the guard itself.
    ///
    /// All gated on <see cref="ArenaEquatorRaid.IsLoaded()"/> — same reasoning as
    /// <see cref="EffectsControllerFrostbitePrefix"/> (these hooks only ever fire once
    /// <c>PlayerCameraControllerCreated</c>/<c>Init</c> have run, i.e. after our scenes are loaded).
    /// </summary>
    internal static class EyeBurnGuardPrefix
    {
        // Shared with EyeBurnPauseGuardPrefix below — same field, same "moved" fallback behaviour.
        internal static readonly FieldInfo EyeBurnField = AccessTools.Field(typeof(EffectsController), "_eyeBurn");
        static bool _logged;

        // Target: EffectsController.OnHealthBurnEyes(Vector3, float, float) — EffectsController.cs:1404-1408.
        // Subscribed at PlayerCameraControllerCreated (:1251, player.HealthController.BurnEyesEvent), raised
        // by ActiveHealthController.DoBurnEyes on every flash/stun grenade the local player has LOS to.
        internal static bool Prefix(EffectsController __instance)
        {
            return !ShouldSkip(__instance);
        }

        static bool ShouldSkip(EffectsController instance)
        {
            if (!ArenaEquatorRaid.IsLoaded())
            {
                return false; // every other map's raid — untouched (retail camera prefabs ship EyeBurn)
            }
            if (EyeBurnField == null)
            {
                return false; // reflection target moved — fall back to vanilla (pre-existing NRE), don't add a new one
            }
            if (EyeBurnField.GetValue(instance) != null)
            {
                return false; // component present (real camera prefab, or already fixed) — run original
            }
            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo("[ArenaMapPort][RaidFix] EffectsController._eyeBurn is null on Cam2 — " +
                                    "skipping eye-burn screen overlay for flash/stun grenades this raid " +
                                    "(fix-combat F1, EffectsController.cs:1327/1404-1408/1260-1263)");
            }
            return true;
        }
    }

    /// <summary>fix-combat F1, second entry point — see <see cref="EyeBurnGuardPrefix"/> doc comment.</summary>
    internal static class EyeBurnPauseGuardPrefix
    {
        // Target: EffectsController.OnClientPlayerEffectsPauseEvent(ClientPlayerEffectsPauseEvent) —
        // EffectsController.cs:1256-1266, dereferences _eyeBurn.IsPaused/.ClearEyeBurnRT() unguarded.
        internal static bool Prefix(EffectsController __instance)
        {
            if (!ArenaEquatorRaid.IsLoaded())
            {
                return true;
            }
            if (EyeBurnGuardPrefix.EyeBurnField == null)
            {
                return true;
            }
            return EyeBurnGuardPrefix.EyeBurnField.GetValue(__instance) != null;
        }
    }

    /// <summary>fix-combat F2 (explosives-items) — see <see cref="EyeBurnGuardPrefix"/> doc comment's shared
    /// root cause. Target: the nested <c>EffectsController.FlashAccumulator.AddEffect(IHealthEffect)</c>
    /// (<c>EffectsController.cs:634-646</c>), constructed with our null <c>_grenadeFlash</c> at
    /// <c>EffectsController.cs:1378</c> and reached whenever a Stun-type health effect (Zarya/Gluhar-type
    /// grenades, <c>ActiveHealthController.DoStun</c>) is added — dereferences <c>_screenEffect.Explode(...)</c>
    /// unguarded at line 644. <c>_screenEffect</c> is <c>public</c> on <c>FlashAccumulator</c>
    /// (<c>EffectsController.cs:622</c>), so no reflection is needed here, unlike the EyeBurn guards above.</summary>
    internal static class FlashAccumulatorGuardPrefix
    {
        static bool _logged;

        internal static bool Prefix(EffectsController.FlashAccumulator __instance)
        {
            if (!ArenaEquatorRaid.IsLoaded())
            {
                return true;
            }
            if (__instance._screenEffect != null)
            {
                return true;
            }
            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo("[ArenaMapPort][RaidFix] EffectsController._grenadeFlash is null on Cam2 — " +
                                    "skipping white-flash screen overlay for stun grenades this raid " +
                                    "(fix-combat F2, EffectsController.cs:1326/634-646)");
            }
            return false;
        }
    }

    /// <summary>
    /// fix-combat, additional finding confirmed alongside F1/F2 (same code-pattern gap the audit's own
    /// methodology missed — flagged by the skeptic pass, independently verified here against
    /// <c>EffectsController.cs</c>). <c>_bloodOnScreen</c> (:1164/1285) and <c>_blur</c> (:1170/1288, a
    /// <c>FastBlur</c>) are fetched with the exact same unguarded <c>GetComponent</c> pattern as EyeBurn/
    /// GrenadeFlashScreenEffect, but dereferenced on a FAR hotter path than flash/stun grenades:
    /// <c>OnHealthApplyDamage</c> (:1410-1419, every bleeding wound —
    /// <c>player.HealthController.ApplyDamageEvent</c>) and <c>OnPlayerDamaged</c> (:1421-1473, almost
    /// every non-Fall damaging hit >= 0.1 — <c>player.OnDamageReceived</c>, the single most frequently
    /// invoked damage callback in this file).
    ///
    /// <c>OnHealthApplyDamage</c>'s only content is the <c>_bloodOnScreen.HitBleeding(...)</c> call
    /// (:1417) — skip-whole-method (prefix, same convention as F1/F2) loses nothing else.
    ///
    /// <c>OnPlayerDamaged</c> is different: <c>ProceduralWeaponAnimation.ForceReact.AddForce(...)</c>
    /// (recoil kick, :1458) and <c>PlaySpecialSound</c> (:1427-1429, glass/helmet ricochet audio) both run
    /// BEFORE the first unguarded dereference (<c>_blur.enabled = true;</c>, :1460) — skipping the whole
    /// method the way F1/F2 do would also silently drop that recoil kick and special-hit audio, which have
    /// nothing to do with the missing screen-effect components. A <c>Finalizer</c>-only guard (no prefix)
    /// lets the original run to completion up to the NRE, so those two side effects still fire, and only
    /// swallows the exception that follows — same technique and same "riskier half, so gate hard"
    /// reasoning as <see cref="SpatialAudioOcclusionGuard"/>/<see cref="SpatialAudioInteractiveOcclusionGuard"/>.
    ///
    /// NOT fixed here (deferred, see docs/progress/fix-combat.md): <c>OnHealthDied</c> (:1488-1497) also
    /// dereferences <c>_blur</c> unguarded (and possibly <c>_deathFade</c>) — but that event fires at most
    /// once per raid (player death), so per this task's own severity taxonomy it is "degraded", not
    /// "error-spam" (no recurring per-shot/per-frame exception), and raid gameplay is already over at that
    /// point regardless.
    /// </summary>
    internal static class CombatScreenEffectsGuard
    {
        static readonly FieldInfo BloodOnScreenField = AccessTools.Field(typeof(EffectsController), "_bloodOnScreen");
        static bool _applyDamageLogged;
        static bool _playerDamagedLogged;

        // Target: EffectsController.OnHealthApplyDamage(EBodyPart, float, DamageInfo) — cs:1410-1419.
        internal static bool Prefix(EffectsController __instance)
        {
            if (!ArenaEquatorRaid.IsLoaded())
            {
                return true;
            }
            if (BloodOnScreenField == null)
            {
                return true; // reflection target moved — vanilla (pre-existing) behaviour
            }
            if (BloodOnScreenField.GetValue(__instance) != null)
            {
                return true;
            }
            if (!_applyDamageLogged)
            {
                _applyDamageLogged = true;
                Plugin.Log.LogInfo("[ArenaMapPort][RaidFix] EffectsController._bloodOnScreen is null on Cam2 — " +
                                    "skipping bleeding screen overlay this raid (fix-combat, " +
                                    "EffectsController.cs:1285/1417)");
            }
            return false;
        }

        // Target: EffectsController.OnPlayerDamaged(float, EBodyPart, EDamageType, float, MaterialType) —
        // cs:1421-1473. Finalizer only — see class doc comment for why a prefix would drop unrelated
        // side effects (recoil kick, special-hit audio) that run before the NRE point.
        internal static Exception Finalizer(Exception __exception)
        {
            if (__exception == null)
            {
                return null;
            }
            if (!ArenaEquatorRaid.IsLoaded())
            {
                return __exception; // vanilla behaviour for every other map's raid
            }
            if (!_playerDamagedLogged)
            {
                _playerDamagedLogged = true;
                Plugin.Log.LogWarning("[ArenaMapPort][RaidFix] EffectsController.OnPlayerDamaged threw " +
                                      "(blur/blood-on-screen hit overlay degraded this raid — recoil kick and " +
                                      $"special-hit audio still play): {__exception.GetType().Name}: " +
                                      $"{__exception.Message} (fix-combat, EffectsController.cs:1288/1460-1470)");
            }
            return null; // swallow — never let this escape into the player-damage callback chain
        }
    }
}
