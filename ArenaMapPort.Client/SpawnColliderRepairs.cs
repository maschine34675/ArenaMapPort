using EFT.Game.Spawning;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// fix-lighting-movement, item 1 — the already-diagnosed "player cannot move, thrown grenade drops
    /// at the player's own feet" bug. Root cause (re-confirmed against the decompile by two independent
    /// verifier passes this pass, docs/progress/fix-lighting-movement.md): AuthorScriptsScene.cs:118-121
    /// authors each SpawnPointMarker's BoxCollider as a solid, ENABLED, NON-trigger 1x2x1 box centered
    /// exactly on the spawn position — the player spawns physically embedded in it (solid geometry
    /// blocks movement; a grenade thrown from inside it collides immediately at the player's feet).
    ///
    /// BSG's own runtime code can never self-heal this for a pre-existing collider:
    ///   - SpawnPointParamsExtension.TryAddSpawnCollider (D:/SPT41/Development/_decompiled/
    ///     EFT.Game.Spawning/SpawnPointParamsExtension.cs:22-36): `Collider component =
    ///     go.GetComponent&lt;Collider&gt;(); if (component != null) { go.TryAddNavMeshModifier();
    ///     return component; }` — early-returns any EXISTING collider completely untouched.
    ///   - AddSpawnCollider (same file, :38-58) — the ONLY call site that ever sets isTrigger=true — is
    ///     unreachable here: it only runs inside the `component == null` branch TryAddSpawnCollider just
    ///     took a different path from.
    ///   - SpawnPointMarker.FixCollider (EFT.Game.Spawning/SpawnPointMarker.cs:257-318), the server-params
    ///     reconciliation pass that runs afterward, only ever adjusts center/size/enabled on an existing
    ///     BoxCollider — isTrigger is never referenced there either.
    /// So the box stays solid forever, every raid, regardless of base.json content — this Harmony
    /// postfix is the only thing that ever flips it, matching the universal "spawn collider is always a
    /// trigger" idiom BSG's own AddSpawnCollider uses and every hand-authored trigger in the MIT-licensed
    /// reference corpus (ManimalIcebreaker/ManimalLabs) follows with zero counterexamples.
    ///
    /// Anchor: SpawnPointMarker.Start() (EFT.Game.Spawning/SpawnPointMarker.cs:96-116) — the same method
    /// that already null/enabled-checks `_collider` right after resolving it, so this fix lands exactly
    /// once per marker, right when the marker's own validation runs, well before spawn-point selection
    /// (BaseLocalGame.Run, which collects markers via SpawnPointsCollection.CreateFromScene — see
    /// SpawnInfiltrationRepairs.cs for that unrelated fix on the same collection).
    ///
    /// Source-side belt-and-suspenders: AuthorScriptsScene.cs:118-121 now also authors
    /// `box.isTrigger = true;` directly (effective from the next bundle rebuild only — no rebuild
    /// performed here per the hard rules). BakeNavMesh.cs's own content-scene-root filter (its "would
    /// otherwise leak into the navmesh" comment) does not depend on this flag either way — see that
    /// file's own updated comment for why.
    /// </summary>
    internal static class SpawnColliderRepairs
    {
        // Target: SpawnPointMarker.Start(), public instance, no args, single overload. Verified:
        // EFT.Game.Spawning/SpawnPointMarker.cs:96-116.
        internal static void Postfix(SpawnPointMarker __instance)
        {
            if (__instance == null || !ArenaEquatorRaid.IsLoaded())
            {
                return; // every other map's raid — untouched
            }

            MakeTrigger(__instance);
        }

        // Second pass from SpawnInfiltrationFixPostfix (CreateFromScene, BaseLocalGame.cs:526), which is
        // guaranteed to run before spawn selection even if Start() ran before IsLoaded() turned true.
        internal static void EnsureAllTriggers()
        {
            foreach (var marker in UnityEngine.Object.FindObjectsOfType<SpawnPointMarker>())
            {
                MakeTrigger(marker);
            }
        }

        static void MakeTrigger(SpawnPointMarker marker)
        {
            var collider = marker._collider != null ? marker._collider : marker.GetComponent<UnityEngine.Collider>();
            if (collider != null && !collider.isTrigger)
            {
                collider.isTrigger = true;
                Plugin.Log.LogInfo($"[ArenaMapPort][RaidFix] set isTrigger=true on " +
                                    $"'{marker.gameObject.name}'s spawn collider (was solid — the player " +
                                    "would otherwise spawn embedded in it, unable to move; see " +
                                    "SpawnColliderRepairs' own doc comment for the full BSG-code root-cause chain)");
            }
        }
    }
}
