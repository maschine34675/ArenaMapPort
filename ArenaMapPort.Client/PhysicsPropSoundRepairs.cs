using System;
using System.Collections.Generic;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// PLAN-TWO-MAPS item 5 follow-up (2026-09-25): impact sounds of the imported physics props must not throw.
    ///
    /// Vanilla: <c>SmallPhysicsObject.OnCollisionHandler</c> (D:/SPT41/Development/_decompiled/
    /// SmallPhysicsObject.cs:249) lazily resolves <c>_collisionSoundBank</c> through <c>SetupSoundBank()</c>
    /// (the WeaponSounds ScriptableObject's <c>&lt;SoundType&gt;ImpactSoundBank</c>) and hands it to
    /// <c>BetterAudio.PlayAtPoint</c>, which dereferences the bank unconditionally (<c>bank.SourceType</c>).
    ///
    /// The ported scene: the first Bay5 host raid (13:13, EFT instance 9f0fe0dc) logged
    /// <c>NullReferenceException: BetterAudio.PlayAtPoint &lt;- SmallPhysicsObject.OnCollisionHandler</c> for
    /// both the player-touch path (<c>CheckPlayerCollision</c>) and the Rigidbody path (<c>OnCollisionEnter</c>):
    /// the props had the class-default SoundType (Books) whose bank came back null. The pipeline now imports
    /// Arena's authored SoundType per prop (extract_physics_props.decode_sound_type, PhysicsPropImporter), which
    /// is the real fix; this prefix is the safety net for any type whose WeaponSounds bank is still null on this
    /// SPT build: it runs the same lazy setup, and if no bank exists it skips only the sound, keeps the vanilla
    /// 0.5 s collision-tracking timer and logs once per SoundType. Everything else (force, fragments, tracking)
    /// stays vanilla. Late callbacks on an owned scene also stay guarded after its Scripts scene
    /// unloads: shared sound assets may already be released while geometry still collides.
    /// Developer diagnostic (NoiseProbe, Audio diagnostics section): every impact is noted in the probe's event ring (played, skipped or muted), and
    /// NoiseProbeMuteProps skips all impact sounds for an A/B listening test.
    /// Vanilla maps are untouched.
    /// </summary>
    internal static class PhysicsPropSoundRepairs
    {
        const string Tag = "[Physics]";
        static readonly HashSet<int> _reported = new HashSet<int>();

        internal static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(SmallPhysicsObject), nameof(SmallPhysicsObject.OnCollisionHandler));
            if (!HarmonyPatches.Verify(target, typeof(SmallPhysicsObject), nameof(SmallPhysicsObject.OnCollisionHandler)))
            {
                return;
            }
            var prefix = AccessTools.Method(typeof(PhysicsPropSoundRepairs), nameof(OnCollisionHandlerPrefix));
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Plugin.Log.LogDebug("[Harmony] patched SmallPhysicsObject.OnCollisionHandler (null impact SoundBank guard)");
        }

        /// <summary>Harmony prefix: <c>___</c> parameters bind the private/protected fields by name.</summary>
        public static bool OnCollisionHandlerPrefix(SmallPhysicsObject __instance, bool resetSound,
            ref SoundBank ____collisionSoundBank, ref float ___IgnoreCollisionTrackingTimer)
        {
            if (!ArenaMapRaid.IsLoaded())
            {
                var scene = __instance.gameObject.scene;
                if (!scene.IsValid() || !ArenaMapRaid.IsOwnedScene(scene.name)) return true;
                // Geometry can outlive Scripts and the shared WeaponSounds/BetterAudio assets.
                // Skip only this late sound callback, even if it still holds a bank reference.
                ___IgnoreCollisionTrackingTimer = Time.time + 0.5f;
                return false;
            }
            if (NoiseProbe.PropsMuted)
            {
                ___IgnoreCollisionTrackingTimer = Time.time + 0.5f; // what vanilla sets after PlayAtPoint
                if (NoiseProbe.Armed) NoiseProbe.NoteProp(__instance, 2);
                return false;
            }
            if (____collisionSoundBank == null || resetSound)
            {
                try
                {
                    __instance.SetupSoundBank();
                }
                catch (Exception ex)
                {
                    ReportOnce(__instance, "SetupSoundBank threw " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            if (____collisionSoundBank != null)
            {
                if (NoiseProbe.Armed) NoiseProbe.NoteProp(__instance, 1);
                return true; // vanilla path plays the sound
            }
            ReportOnce(__instance, "WeaponSounds has no impact SoundBank for this type; prop stays silent");
            ___IgnoreCollisionTrackingTimer = Time.time + 0.5f; // what vanilla would have set after PlayAtPoint
            if (NoiseProbe.Armed) NoiseProbe.NoteProp(__instance, 0);
            return false;
        }

        static void ReportOnce(SmallPhysicsObject prop, string what)
        {
            int key = (int)prop.SoundType;
            if (!_reported.Add(key))
            {
                return;
            }
            Plugin.Log.LogWarning($"{Tag} impact sound skipped for SoundType={prop.SoundType} ('{prop.name}'): {what}");
        }
    }
}
