using EFT.Game.Spawning;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// fix-gamestart, finding F1 (blocker) — fixes the raid-start hang: the 10s pre-raid countdown never
    /// finishes because SPT's own client patch crashes reading our spawn points.
    ///
    /// Root cause chain (all file:line against D:/SPT41/Development/_decompiled unless noted):
    ///   1. The deployed scenes bundle was built while AuthorScriptsScene.cs still authored all 3
    ///      SpawnPointMarkers with <c>spawnPoint.Infiltration = null;</c> (the source now authors
    ///      <see cref="InfiltrationName"/>, effective from the next bundle rebuild).
    ///   2. <c>SpawnPoint.Infiltration</c> is a plain mutable public field (EFT.Game.Spawning/SpawnPoint.cs:23);
    ///      <c>SpawnPointMarker.SpawnPoint</c> (SpawnPointMarker.cs:58) returns that exact same instance every
    ///      call — nothing downstream ever copies it.
    ///   3. <c>BaseLocalGame&lt;T&gt;.Run</c> collects every marker into an <c>ISpawnPoint</c> set via
    ///      <c>SpawnPointsCollection.CreateFromScene(..., location.SpawnPointParams)</c>
    ///      (EFT/BaseLocalGame.cs:526). Our base.json ships no <c>SpawnPointParams</c> (by design — see
    ///      analysis/spt-location-contract.md §7 step 2), so <c>CreateFromScene</c>'s <c>parameters != null</c>
    ///      branch (SpawnPointsCollection.cs:157) — the ONLY other code that ever writes
    ///      <c>SpawnPoint.Infiltration</c> (via <c>SpawnPointMarker.FixParams</c>) — never runs. Nothing
    ///      overwrites the null the scene authored.
    ///   4. <c>spawnSystem.SelectSpawnPoint(...)</c> later returns one of those exact instances and
    ///      <c>_entryPoint = spawnPoint.Infiltration</c> (BaseLocalGame.cs:577, duplicated at :179) is null.
    ///   5. Vanilla tolerates this: <c>vmethod_6</c> just skips the exfil-init block when
    ///      <c>_entryPoint == null</c> (BaseLocalGame.cs:790, <c>if (_entryPoint != null)</c>). SPT's own
    ///      Harmony PREFIX on that same method,
    ///      <c>SPT.SinglePlayer.Patches.RaidFix.EmptyInfilFixPatch.PatchPrefix</c> (spt-client-decompiled
    ///      .../spt-singleplayer/SPT.SinglePlayer.Patches.RaidFix/EmptyInfilFixPatch.cs:20-49), does NOT: when
    ///      <c>____entryPoint</c> is null/whitespace it scans <c>Resources.FindObjectsOfTypeAll&lt;
    ///      SpawnPointMarker&gt;()</c> for any marker with non-empty <c>Infiltration</c> (:27-35) and
    ///      unconditionally dereferences the nearest match at line 48
    ///      (<c>spawnPointMarker.SpawnPoint.Infiltration</c>) with NO null check on <c>spawnPointMarker</c>
    ///      itself. Since all 3 of our markers have Infiltration=null, the candidate list is empty,
    ///      <c>spawnPointMarker</c> stays null, and line 48 throws — this is the exact NullReferenceException
    ///      in D:\SPT41vanilla\BepInEx\LogOutput.log (EmptyInfilFixPatch.PatchPrefix &lt;- vmethod_6 &lt;-
    ///      BaseLocalGame+CG_Class1638.MoveNext), which is the last line ever logged that session — the raid
    ///      hangs because nothing after this coroutine step ever runs.
    ///
    /// FIX: a Harmony postfix on <see cref="SpawnPointsCollection.CreateFromScene"/> — the single call site
    /// (BaseLocalGame.cs:526) where every one of our SpawnPointMarkers is first collected into the
    /// <c>ISpawnPoint</c> set everything downstream reads from (spawn selection AND EmptyInfilFixPatch's own
    /// rescan both key off the identical instances). Runs strictly before <c>SelectSpawnPoint</c> is ever
    /// called (:529) and long before <c>vmethod_6</c>/EmptyInfilFixPatch (fired from the raid-start coroutine
    /// well after <c>Run</c> returns), so by the time either reads <c>Infiltration</c> it is already set.
    ///
    /// Source-side belt-and-suspenders: AuthorScriptsScene.cs:132 now authors <see cref="InfiltrationName"/>
    /// directly too (next bundle rebuild only — no rebuild performed here per the hard rules). Both literals
    /// MUST stay in sync; AuthorScriptsScene.cs runs in the Unity Editor assembly domain and cannot reference
    /// this assembly, so it duplicates the string rather than sharing a constant (same pattern that file
    /// already uses for its own SpawnDefNames/SpawnDefSides literals).
    /// </summary>
    internal static class SpawnInfiltrationFixPostfix
    {
        // Target: SpawnPointsCollection.CreateFromScene(DateTime?, SpawnPointParams[]), static, single
        // overload, [NotNull] return. Verified: EFT.Game.Spawning/SpawnPointsCollection.cs:152-170 — the
        // dictionary is keyed on marker.SpawnPoint itself (:169), the identical instance every marker's
        // Infiltration field lives on, so mutating it here through the IEnumerable<ISpawnPoint> the
        // collection exposes reaches the same object BaseLocalGame reads from later.
        internal static void Postfix(SpawnPointsCollection __result)
        {
            // Multimap generalization (docs/progress/multimap-generalize.md): InfiltrationName now
            // comes from the currently-loaded map's own descriptor (ArenaMapRegistry.Equator.
            // InfiltrationName == this file's old "ArenaEquator" literal, byte-identical) instead of
            // one hardcoded const, so each map can (and Bay5 does) use its own Infiltration string. Must
            // stay in sync with AuthorScriptsScene.cs's own per-map literal (Editor assembly domain,
            // cannot reference ArenaMapPort.Shared — see that file for the cross-assembly duplicate),
            // and with any future base.json Exit.EntryPoints CSV entries (none exist yet — exits are
            // empty by design this milestone).
            var map = ArenaMapRaid.CurrentMap;
            if (__result == null || map == null)
            {
                return; // every other map's raid — untouched
            }

            SpawnColliderRepairs.EnsureAllTriggers();
            SpawnPlacementRepairs.Apply(__result);

            int fixedCount = 0;
            foreach (var point in __result)
            {
                // bots-stage1: server-shipped Bot-category points carry Infiltration "" on purpose (vanilla
                // convention); only player points need the map's infiltration for SPT's EmptyInfilFixPatch.
                if (point is SpawnPoint concrete && string.IsNullOrEmpty(concrete.Infiltration)
                    && (concrete.Categories & ESpawnCategoryMask.Player) != 0)
                {
                    concrete.Infiltration = map.InfiltrationName;
                    fixedCount++;
                }
            }

            if (fixedCount > 0) // once per raid — CreateFromScene runs exactly once per BaseLocalGame.Run
            {
                Plugin.Log.LogInfo($"[ArenaMapPort][RaidFix] set Infiltration=\"{map.InfiltrationName}\" on " +
                                    $"{fixedCount} SpawnPointMarker(s) with no Infiltration authored " +
                                    "(prevents SPT.SinglePlayer.Patches.RaidFix.EmptyInfilFixPatch's NRE " +
                                    "at raid-start countdown end)");
            }
        }
    }
}
