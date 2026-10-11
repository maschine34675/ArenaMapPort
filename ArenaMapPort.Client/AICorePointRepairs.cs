using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Game.Spawning;
using HarmonyLib;
using UnityEngine;

namespace ArenaMapPort.Client
{
    /// <summary>
    /// bots-stage1 (docs/progress/bots-stage1.md, "Host test 4") — the missing core-point net.
    ///
    /// Why bots never came alive on Equator although waves fired, points validated and a scav body appeared:
    /// <c>BotCreatorClient.CreateBot</c> (BotCreatorClient.cs:135-147) awaits <c>LocalPlayer.Create</c> (the
    /// body) and then calls <c>CoversData.AICorePointsHolder.GetCorePoint(bornInfo.CorePointId)</c>
    /// (AICorePointHolder.cs:112: <c>CorePoints.FirstOrDefault(...)</c>). Our scenes ship no
    /// <c>AICorePointHolder</c>, so <c>AICoversData.CreateOrFind</c> (AICoversData.cs:300-308) AddComponents
    /// a fresh one whose <c>public List&lt;AICorePoint&gt; CorePoints;</c> (AICorePointHolder.cs:61) has no
    /// initializer — null — and the LINQ call throws ArgumentNullException inside an <c>async Task</c> whose
    /// only observer is <c>.HandleExceptions()</c>: nothing in any log, the un-hidden body stays where it
    /// spawned (<c>SwitchBotVisual(false)</c> never ran), <c>BotOwner.Create</c> is never reached and the
    /// ghost then blocks its own spawn point for every later wave (<c>IsNotCollided</c> counts it as a
    /// non-AI player). Host raids 2–4 on 2026-09-19 all showed exactly this signature.
    ///
    /// Fix, before <c>BotsController.Init</c> runs (an ADDITIONAL prefix next to §F.2 and the stationary
    /// controller ensure): make sure a holder exists, its list is non-null, and it holds at least one
    /// <see cref="AICorePoint"/> — id 0 / connection group 0, the ids our server-shipped
    /// <c>SpawnPointParams.CorePointId</c> carry, placed at the centroid of the Bot spawn markers. With one
    /// core point every <c>GetCorePoint(0)</c>/<c>GetClosest</c> resolves to it and
    /// <c>BotOwner.StartCorePoint.ConnectionGroupId</c> (BotCoversData.cs:196, BotPathFinderCorePoints.cs:513,
    /// LootPatrolLayer.cs:424, …) never dereferences null. There are no <c>GroupPoint</c>s (covers) on our
    /// maps, so cover searches simply return nothing — SAIN moves bots on the NavMesh without them.
    ///
    /// Host test 5 (21:30) got past that and died one step later in <c>BotOwner.PreActivate</c>:
    /// <c>BotGameEventsData..ctor → KhorovodBotGameEvent..ctor</c> iterates
    /// <c>CoversData.AIPlaceInfoHolder.Places</c> — the same null-list shape on the next holder
    /// (AIPlaceInfoHolder.cs:19, no initializer). <c>AICoversData.CreateOrFind</c> (AICoversData.cs:284-370)
    /// AddComponents eight such holders when the scene has none: AIManualPointsHolder (its list IS
    /// initialized), AICorePointHolder, AIMinesPositionsHolder, AIDangerPlacesHolder, AIPlaceInfoHolder,
    /// AIPatrolsData, AIVoxelesData, AICoversData. Instead of chasing them one raid at a time,
    /// <see cref="EnsureCoversHolders"/> creates every one of them first (so CreateOrFind finds ours) and
    /// fills EVERY null <c>List&lt;&gt;</c>/array/<c>Dictionary&lt;,&gt;</c> instance field with an empty instance
    /// via reflection — the semantics BSG's own scene-YAML deserialization would give an authored-but-empty
    /// holder. Static fields and non-collection fields are never touched.
    ///
    /// Host test 7 (21:57, activation-step finalizers): with every holder present the bot registers and then
    /// <c>BotOwner.method_10</c> (the ~45-step activation, wrapped in <c>catch { BotState = ActiveFail; }</c>)
    /// dies in <c>BotMover.Activate → BotVoxelesPersonalData.GetVoxelSafe</c>: the voxel grid is
    /// <c>NavGraphVoxelSimple[0,0,0]</c> and <c>Mathf.Clamp(i, 0, MaxX - 1)</c> yields -1 → IndexOutOfRange.
    /// An ActiveFail bot stands at its spawn point forever with no line anywhere. <see cref="EnsureVoxelGrid"/>
    /// therefore authors a coarse grid (BSG's own 10 × 5 × 10 m cells, NavGraphVoxelBase.SIZE/SIZE_Y) over the
    /// spawn markers' bounds plus a margin, one empty <see cref="NavGraphVoxelSimple"/> per cell with
    /// <c>HaveNavMesh</c> set; <c>AIVoxelesData.RestoreData</c> then builds the 3-D array from it exactly as for a
    /// baked map. No cell carries covers (<c>PointStartSearch</c> null), which is what "no cover data" means.
    ///
    /// Belt and braces: a postfix on <c>BotOwner.Create</c> assigns the nearest existing core point when the
    /// id lookup still returned null (a marker with a foreign CorePointId). Adapted from ManimalIcebreaker's
    /// <c>Patch_BotStartCorePoint</c> (RaidFixPatches.cs:4622-4647, MIT — attribution in
    /// THIRD-PARTY-NOTICES.md). Vanilla maps: every method here returns immediately.
    ///
    /// PLAN-TWO-MAPS item 8 (docs/progress/two-maps-item-8-ai-anchors.md): when <see cref="AiAnchors"/>
    /// loads a sidecar for the current map (Equator/Bay5 only), <see cref="EnsureCorePoint"/> authors
    /// Arena's own real <see cref="AICorePoint"/> net (ids/connection groups/positions, connections left
    /// empty — see AiAnchors' own doc comment) in place of the single synthetic point below, PLUS the
    /// same synthetic id-0/group-0 alias still added regardless: <c>tools/server/make_base_json.py</c>
    /// gives every <c>SpawnPointParams.CorePointId</c> the value 0, while Arena's real ids start at 1, so
    /// without the alias <c>GetCorePoint(0)</c> would return null and bot creation would regress exactly
    /// as it did before this whole file existed. Any exception while authoring the real net falls
    /// through to the untouched single-point synthesis (the twelve deferred maps' own behavior).
    /// </summary>
    internal static class AICorePointFallback
    {
        const string Tag = "[ArenaMapPort][RaidFix]";
        internal const string HolderName = "AICorePointHolder(ArenaFallback)";
        internal const int FallbackId = 0;
        internal const int FallbackGroup = 0;

        internal static void Install(Harmony harmony)
        {
            var init = AccessTools.Method(typeof(BotsController), nameof(BotsController.Init));
            var create = AccessTools.Method(typeof(BotOwner), nameof(BotOwner.Create));
            var get = AccessTools.Method(typeof(AICorePointHolder), nameof(AICorePointHolder.GetCorePoint));
            if (init == null || create == null || get == null)
            {
                Plugin.Log.LogError($"{Tag} AICorePointFallback: target missing (BotsController.Init={init != null} " +
                                    $"BotOwner.Create={create != null} AICorePointHolder.GetCorePoint={get != null}) — bots on " +
                                    "ported maps will silently fail to activate (null CorePoints).");
                return;
            }
            harmony.Patch(init, prefix: new HarmonyMethod(AccessTools.Method(typeof(AICorePointFallback), nameof(InitPrefix))));
            harmony.Patch(get, prefix: new HarmonyMethod(AccessTools.Method(typeof(AICorePointFallback), nameof(GetCorePointPrefix))));
            harmony.Patch(create, postfix: new HarmonyMethod(AccessTools.Method(typeof(AICorePointFallback), nameof(CreatePostfix))));
            Plugin.Log.LogDebug("[ArenaMapPort][Harmony] patched BotsController.Init (core-point fallback, additional prefix), " +
                               "AICorePointHolder.GetCorePoint (null-list guard), BotOwner.Create (StartCorePoint fallback) — bots-stage1");
        }

        // BotsController.Init(IBotGame botGame, IBotCreator botCreator, BotZone[] botZones, ...) — the
        // parameter is bound by name; only botZones is needed.
        static void InitPrefix(BotZone[] botZones)
        {
            if (!ArenaMapRaid.IsLoaded())
            {
                return;
            }
            try
            {
                SurveyMarkers(botZones, out Vector3 anchor, out Bounds bounds, out string basis);
                EnsureCoversHolders();
                EnsureVoxelGrid(bounds);
                var map = ArenaMapRaid.CurrentMap;
                var anchors = AiAnchors.Load(map); // null for every map besides Equator/Bay5, or on a bad sidecar
                bool authoredFromAnchors = EnsureCorePoint(anchor, basis, anchors, out int realCorePointCount);
                PatrolWayFallback.EnsurePatrolWays(botZones, authoredFromAnchors ? anchors : null,
                                                   out int hubCount, out int subPointCount); // see PatrolWayRepairs.cs
                if (authoredFromAnchors)
                {
                    Plugin.Log.LogDebug($"{Tag} {map.Key}: authored {realCorePointCount} real AI core point(s), " +
                                       $"{hubCount} patrol hub(s), {subPointCount} sub point(s) from " +
                                       $"ai/{anchors.MapKey}.ai.json, plus the synthetic id-0/group-0 alias " +
                                       "(SpawnPointParams.CorePointId is always 0)");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"{Tag} core-point fallback failed: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
        }

        // The holder MonoBehaviours AICoversData.CreateOrFind AddComponents when the scene ships none
        // (AICoversData.cs:284-370), in its own order. AICoversData itself last: it references the others.
        static readonly Type[] HolderTypes =
        {
            typeof(AIManualPointsHolder), typeof(AICorePointHolder), typeof(AIMinesPositionsHolder),
            typeof(AIDangerPlacesHolder), typeof(AIPlaceInfoHolder), typeof(AIPatrolsData),
            typeof(AIVoxelesData), typeof(AICoversData),
        };

        internal static void EnsureCoversHolders()
        {
            var created = new List<string>();
            var filled = new List<string>();
            foreach (var type in HolderTypes)
            {
                var component = UnityEngine.Object.FindObjectOfType(type) as Component;
                if (component == null)
                {
                    component = new GameObject(type.Name + "(ArenaFallback)").AddComponent(type);
                    created.Add(type.Name);
                }
                try
                {
                    FillNullCollections(component, filled);
                }
                catch (Exception e)
                {
                    // one odd field must not stop the other holders (host test 6: a rank-3 array aborted the whole ensure)
                    Plugin.Log.LogWarning($"{Tag} could not initialize null collections on {type.Name}: {e.GetType().Name}: {e.Message}");
                }
            }
            if (created.Count > 0 || filled.Count > 0)
            {
                Plugin.Log.LogDebug($"{Tag} AI covers holders: created {created.Count} missing component(s) " +
                                   $"[{string.Join(", ", created)}]; initialized {filled.Count} null collection field(s) " +
                                   $"[{string.Join(", ", filled)}] — no baked bot-nav data on this map, empty holders are the " +
                                   "authored-but-empty shape BSG's own deserialization would give");
            }
        }

        /// <summary>Every null instance field of type List&lt;T&gt;, T[] or Dictionary&lt;K,V&gt; becomes an empty instance.</summary>
        internal static void FillNullCollections(object component, List<string> filled)
        {
            if (component == null)
            {
                return;
            }
            var type = component.GetType();
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var ft = field.FieldType;
                object empty = null;
                if (ft.IsArray)
                {
                    if (field.GetValue(component) != null) continue;
                    // rank-aware: AIVoxelesData.VoxelesArray is NavGraphVoxelSimple[,,] (a 1-D instance is not assignable)
                    empty = Array.CreateInstance(ft.GetElementType(), new int[ft.GetArrayRank()]);
                }
                else if (ft.IsGenericType && !ft.IsAbstract &&
                         (ft.GetGenericTypeDefinition() == typeof(List<>) || ft.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
                {
                    if (field.GetValue(component) != null) continue;
                    empty = Activator.CreateInstance(ft);
                }
                if (empty != null)
                {
                    field.SetValue(component, empty);
                    filled.Add($"{type.Name}.{field.Name}");
                }
            }
        }

        /// <summary>Returns true iff a real AI-anchors core-point net was authored this call
        /// (<paramref name="realCorePointCount"/> is then &gt; 0); false for the untouched single-point
        /// synthesis (no anchors, empty anchors, an authoring exception, or a no-op re-entry). The decompile
        /// has one <c>BotsController.Init</c> call site per raid (<c>EFT/LocalGame.cs:184</c>); the first check
        /// below (holder already has core points) keeps the method idempotent by construction anyway.</summary>
        internal static bool EnsureCorePoint(Vector3 position, string basis, AiAnchors anchors, out int realCorePointCount)
        {
            realCorePointCount = 0;
            var holder = UnityEngine.Object.FindObjectOfType<AICorePointHolder>();
            bool createdHolder = false;
            if (holder == null)
            {
                holder = new GameObject(HolderName).AddComponent<AICorePointHolder>();
                createdHolder = true;
            }
            if (holder.CorePoints == null)
            {
                holder.CorePoints = new List<AICorePoint>();
            }
            if (holder.CorePoints.Count > 0)
            {
                if (createdHolder)
                {
                    Plugin.Log.LogDebug($"{Tag} created {HolderName} (scene had none)");
                }
                return false; // already populated by an earlier call this raid — idempotent no-op, no new log line
            }

            if (anchors != null && anchors.CorePoints.Count > 0)
            {
                try
                {
                    foreach (var cp in anchors.CorePoints)
                    {
                        var realGo = new GameObject($"AICorePoint(Arena) {cp.Id}");
                        realGo.transform.position = cp.Position;
                        var realPoint = realGo.AddComponent<AICorePoint>();
                        realPoint.ClearConnections(); // no edge data — see AiAnchors.cs's own doc comment
                        realPoint.SetIds(cp.Id, cp.ConnectionGroupId);
                        holder.AddCorePoint(realPoint);
                    }
                    AddAlias(holder, position, out _);
                    realCorePointCount = anchors.CorePoints.Count;
                    return true;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"{Tag} {anchors.MapKey}: authoring the real AI core-point net from " +
                                          $"ai/{anchors.MapKey}.ai.json failed ({e.GetType().Name}: {e.Message}) — " +
                                          "falling back to the single synthetic core point");
                    foreach (Transform child in holder.transform)
                    {
                        UnityEngine.Object.Destroy(child.gameObject);
                    }
                    holder.CorePoints.Clear();
                }
            }

            AddAlias(holder, position, out var addedAlias);
            if (addedAlias)
            {
                Plugin.Log.LogDebug($"{Tag} AICorePointHolder had no core points (no baked bot-nav data for this map) — added " +
                                   $"synthetic AICore ID:{FallbackId} CG:{FallbackGroup} at {position:F1} ({basis}); " +
                                   "GetCorePoint(0) now resolves and BotOwner.StartCorePoint is never null" +
                                   (createdHolder ? $" (holder {HolderName} created as well)" : ""));
            }
            return false;
        }

        /// <summary>The synthetic id-0/group-0 alias (see this class' own doc comment for why it is
        /// ALWAYS added, real anchors or not) — factored out so both the real-anchors path and the
        /// pre-existing single-point synthesis share exactly one place that creates it.</summary>
        static void AddAlias(AICorePointHolder holder, Vector3 position, out bool added)
        {
            added = false;
            foreach (var existing in holder.CorePoints)
            {
                if (existing != null && existing.Id == FallbackId)
                {
                    return; // already present (defensive — no known path adds it twice)
                }
            }
            var go = new GameObject("AICorePoint(ArenaFallback)");
            go.transform.position = position;
            var point = go.AddComponent<AICorePoint>();
            point.ClearConnections();               // the two connection lists, otherwise null too
            point.SetIds(FallbackId, FallbackGroup); // "AICore ID:0 CG:0" — matches SpawnPointParams.CorePointId
            holder.AddCorePoint(point);
            added = true;
        }

        /// <summary>Centroid of the Bot spawn markers (anchor for the core point) and the bounds of ALL
        /// spawn markers (players included — the grid must cover where bots will walk to).</summary>
        static void SurveyMarkers(BotZone[] botZones, out Vector3 anchor, out Bounds bounds, out string basis)
        {
            Vector3 botSum = Vector3.zero;
            int bots = 0, all = 0;
            bounds = default;
            foreach (var marker in UnityEngine.Object.FindObjectsOfType<SpawnPointMarker>())
            {
                var sp = marker?.SpawnPoint;
                if (sp == null)
                {
                    continue;
                }
                Vector3 p = marker.transform.position;
                bounds = all == 0 ? new Bounds(p, Vector3.zero) : Encapsulate(bounds, p);
                all++;
                if ((sp.Categories & ESpawnCategoryMask.Bot) != 0)
                {
                    botSum += p;
                    bots++;
                }
            }
            if (bots > 0)
            {
                anchor = botSum / bots;
                basis = $"centroid of {bots} Bot spawn marker(s)";
                return;
            }
            if (botZones != null)
            {
                foreach (var zone in botZones)
                {
                    if (zone != null)
                    {
                        anchor = zone.transform.position;
                        basis = $"BotZone '{zone.name}' transform";
                        if (all == 0) bounds = new Bounds(anchor, Vector3.zero);
                        return;
                    }
                }
            }
            anchor = Vector3.zero;
            basis = "origin (no markers, no zones)";
            if (all == 0) bounds = new Bounds(anchor, Vector3.zero);
        }

        static Bounds Encapsulate(Bounds b, Vector3 p)
        {
            b.Encapsulate(p);
            return b;
        }

        // BSG's voxel cell (NavGraphVoxelBase.SIZE / SIZE_Y) and the margin around the markers' bounds.
        const float CellXZ = 10f, CellY = 5f;
        static readonly Vector3 GridMargin = new Vector3(60f, 20f, 60f);
        const int MaxCellsXZ = 40, MaxCellsY = 16; // 40 × 16 × 40 = 25 600 empty voxels at most

        internal static void EnsureVoxelGrid(Bounds markerBounds)
        {
            var voxels = UnityEngine.Object.FindObjectOfType<AIVoxelesData>();
            if (voxels == null)
            {
                return; // EnsureCoversHolders creates it; nothing to do if even that failed
            }
            if (voxels.VoxelsList == null)
            {
                voxels.VoxelsList = new List<NavGraphVoxelSimple>();
            }
            if (voxels.VoxelsList.Count > 0)
            {
                return; // the scene ships baked voxels — never touch them
            }

            Vector3 min = markerBounds.min - GridMargin;
            Vector3 max = markerBounds.max + GridMargin;
            int nx = Mathf.Clamp(Mathf.CeilToInt((max.x - min.x) / CellXZ) + 1, 1, MaxCellsXZ);
            int ny = Mathf.Clamp(Mathf.CeilToInt((max.y - min.y) / CellY) + 1, 1, MaxCellsY);
            int nz = Mathf.Clamp(Mathf.CeilToInt((max.z - min.z) / CellXZ) + 1, 1, MaxCellsXZ);

            voxels.MinVoxelesValues = min;
            voxels.SetMaxValues(new Vector3(nx, ny, nz));
            int id = 1;
            for (int x = 0; x < nx; x++)
            for (int y = 0; y < ny; y++)
            for (int z = 0; z < nz; z++)
            {
                var center = min + new Vector3((x + 0.5f) * CellXZ, (y + 0.5f) * CellY, (z + 0.5f) * CellXZ);
                var voxel = new NavGraphVoxelSimple(center, x, y, z, id++)
                {
                    HaveNavMesh = true,
                    DoorLinks = new List<NavMeshDoorLink>(), // the one list without an initializer
                };
                voxels.VoxelsList.Add(voxel);
            }
            Plugin.Log.LogDebug($"{Tag} AIVoxelesData had no voxels (no baked bot-nav data) — authored an empty {nx}×{ny}×{nz} grid " +
                               $"({voxels.VoxelsList.Count} cells of {CellXZ}×{CellY}×{CellXZ} m) from min={min:F1} over the spawn markers' " +
                               $"bounds {markerBounds.min:F0}..{markerBounds.max:F0} + margin {GridMargin}; BotVoxelesPersonalData.GetVoxelSafe " +
                               "now resolves for every position instead of indexing a 0×0×0 array");
        }

        // AICorePointHolder.GetCorePoint(int corePointId) — instance; the unpatched body dereferences
        // CorePoints without a null check.
        static void GetCorePointPrefix(AICorePointHolder __instance)
        {
            if (__instance != null && __instance.CorePoints == null && ArenaMapRaid.IsLoaded())
            {
                __instance.CorePoints = new List<AICorePoint>();
                Plugin.Log.LogWarning($"{Tag} AICorePointHolder.CorePoints was still null at GetCorePoint — initialized empty " +
                                      "(the Init prefix did not run first?)");
            }
        }

        // BotOwner.Create(...) postfix — adapted from ManimalIcebreaker Patch_BotStartCorePoint (MIT).
        static void CreatePostfix(BotOwner __result)
        {
            if (!ArenaMapRaid.IsLoaded() || __result == null || __result.StartCorePoint != null)
            {
                return;
            }
            try
            {
                var covers = UnityEngine.Object.FindObjectOfType<AICoversData>();
                var cores = covers != null && covers.AICorePointsHolder != null ? covers.AICorePointsHolder.CorePoints : null;
                if (cores == null || cores.Count == 0)
                {
                    Plugin.Log.LogWarning($"{Tag} '{__result.name}' has no StartCorePoint and the map has no core points at all — " +
                                          "cover/path code will NRE; the Init prefix should have added one.");
                    return;
                }
                var player = __result.GetPlayer;
                Vector3 pos = player != null ? player.Transform.position : cores[0].Position;
                AICorePoint best = null;
                float bestD = float.MaxValue;
                foreach (var c in cores)
                {
                    if (c == null) continue;
                    float d = (c.Position - pos).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = c; }
                }
                __result.StartCorePoint = best;
                Plugin.Log.LogDebug($"{Tag} assigned StartCorePoint {best?.Id} to '{__result.name}' ({Mathf.Sqrt(bestD):F1} m away) — " +
                                   "its spawn point carried a CorePointId the holder does not know");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{Tag} StartCorePoint fallback failed for '{__result.name}': {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
